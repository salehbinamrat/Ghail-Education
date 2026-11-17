using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>
/// Phase 3. A reconciliation is a read-only, year-scoped comparison of authoritative
/// operational tables with a distinct approved census submission. Nothing is synchronized
/// into an approved submission. Queries are re-evaluated after every write, not cached.
/// </summary>
public sealed partial class SqliteStore
{
    /// <summary>
    /// The final approval is a historical snapshot. Database-level guards protect it
    /// from direct SQL updates too; audit decisions may still be appended when the
    /// district approves the form. This migration never rewrites old submissions.
    /// </summary>
    private static void MigratePhase3Protection(SqliteConnection con)
    {
        using var cmd = Cmd(con, null, """
            CREATE TRIGGER IF NOT EXISTS p3_approved_submission_update
              BEFORE UPDATE ON submissions WHEN OLD.status='approved'
            BEGIN SELECT RAISE(ABORT,'Approved census is immutable; submit an authorized revision'); END;

            CREATE TRIGGER IF NOT EXISTS p3_approved_submission_delete
              BEFORE DELETE ON submissions WHEN OLD.status='approved'
            BEGIN SELECT RAISE(ABORT,'Approved census cannot be deleted'); END;

            CREATE TRIGGER IF NOT EXISTS p3_approved_enrollment_insert
              BEFORE INSERT ON enrollment_rows
              WHEN EXISTS(SELECT 1 FROM submissions s
                          WHERE s.id=NEW.submission_id AND s.status='approved')
            BEGIN SELECT RAISE(ABORT,'Cannot add rows to approved census'); END;

            CREATE TRIGGER IF NOT EXISTS p3_approved_enrollment_update
              BEFORE UPDATE ON enrollment_rows
              WHEN EXISTS(SELECT 1 FROM submissions s
                          WHERE s.id=OLD.submission_id AND s.status='approved')
                OR EXISTS(SELECT 1 FROM submissions s
                          WHERE s.id=NEW.submission_id AND s.status='approved')
            BEGIN SELECT RAISE(ABORT,'Cannot edit approved enrollment totals'); END;

            CREATE TRIGGER IF NOT EXISTS p3_approved_enrollment_delete
              BEFORE DELETE ON enrollment_rows
              WHEN EXISTS(SELECT 1 FROM submissions s
                          WHERE s.id=OLD.submission_id AND s.status='approved')
            BEGIN SELECT RAISE(ABORT,'Cannot delete approved enrollment totals'); END;

            CREATE TRIGGER IF NOT EXISTS p3_approved_decisions_update
              BEFORE UPDATE ON decisions
              WHEN EXISTS(SELECT 1 FROM submissions s
                          WHERE s.id=OLD.submission_id AND s.status='approved')
                OR EXISTS(SELECT 1 FROM submissions s
                          WHERE s.id=NEW.submission_id AND s.status='approved')
            BEGIN SELECT RAISE(ABORT,'Cannot rewrite approved audit history'); END;

            CREATE TRIGGER IF NOT EXISTS p3_approved_decisions_delete
              BEFORE DELETE ON decisions
              WHEN EXISTS(SELECT 1 FROM submissions s
                          WHERE s.id=OLD.submission_id AND s.status='approved')
            BEGIN SELECT RAISE(ABORT,'Cannot erase approved audit history'); END;

            CREATE TRIGGER IF NOT EXISTS p3_approved_round_retarget
              BEFORE UPDATE OF academic_year ON census_rounds
              WHEN EXISTS(SELECT 1 FROM submissions s
                          WHERE s.round_id=OLD.id AND s.status='approved')
            BEGIN SELECT RAISE(ABORT,'Cannot move approved census to another academic year'); END;
            """);
        cmd.ExecuteNonQuery();
    }

    private static int Count64(SqliteDataReader reader, int index) =>
        checked((int)reader.GetInt64(index));

    private static void RequireStatisticsAccess(UserAccount actor, string? schoolId = null)
    {
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewStudents) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewStaff) ||
            (schoolId is not null && !AccessPolicy.CanSeeSchool(actor, schoolId)))
            throw new UnauthorizedAccessException("إظهار هذه البيانات يتطلب صلاحية المدارس والطلاب والكادر في نطاق المدرسة.");
    }

    private static void EnsureYearExists(SqliteConnection con, SqliteTransaction? tx, string year)
    {
        using var cmd = Cmd(con, tx, "SELECT EXISTS(SELECT 1 FROM academic_years WHERE year=$y)",
            ("$y", year));
        if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new ArgumentException("العام الدراسي المطلوب غير مسجل.", nameof(year));
    }

    private static SchoolOperationalSnapshot ReadOperationalSnapshot(
        SqliteConnection con, SqliteTransaction? tx, string schoolId, string year, string today, bool currentYear)
    {
        // The payroll dimension is the original employee identity. A secondment is
        // an assignment of that same person, never a duplicate school_staff record.
        using var cmd = Cmd(con, tx, """
            SELECT s.name,
                (SELECT COUNT(DISTINCT h.student_id)
                 FROM student_history h JOIN school_students st ON st.id=h.student_id
                 WHERE h.school_id=s.id AND h.academic_year=$year
                   AND h.status='مسجل' AND h.end_utc IS NULL
                   AND (st.is_active=1 OR $current=0)),
                (SELECT COUNT(DISTINCT h.student_id)
                 FROM student_history h JOIN school_students st ON st.id=h.student_id
                 WHERE h.school_id=s.id AND h.academic_year=$year
                   AND h.status='مسجل' AND h.end_utc IS NULL
                   AND (st.is_active=1 OR $current=0)
                   AND st.gender='ذكور'),
                (SELECT COUNT(DISTINCT h.student_id)
                 FROM student_history h JOIN school_students st ON st.id=h.student_id
                 WHERE h.school_id=s.id AND h.academic_year=$year
                   AND h.status='مسجل' AND h.end_utc IS NULL
                   AND (st.is_active=1 OR $current=0)
                   AND st.gender='إناث'),
                (SELECT COUNT(*) FROM school_grades g
                 WHERE g.school_id=s.id AND g.academic_year=$year),
                (SELECT COUNT(DISTINCT st.id) FROM school_staff st
                 JOIN employee_identities e ON e.id=st.id
                 WHERE e.original_payroll_school_id=s.id AND st.is_active=1),
                (SELECT COUNT(DISTINCT st.id) FROM school_staff st
                 JOIN employee_assignments a ON a.employee_id=st.id
                 WHERE a.workplace_school_id=s.id AND st.is_active=1
                   AND a.assignment_type<>'إجازة'
                   AND (a.start_date IS NOT NULL OR
                        a.notes NOT IN ('سجل أصلي','سجل أصلي مرحل') OR
                        EXISTS(SELECT 1 FROM staff_personnel_details p
                               WHERE p.staff_id=st.id AND p.assignment_status='مباشر'))
                   AND (a.start_date IS NULL OR a.start_date<=$today)
                   AND (a.end_date IS NULL OR a.end_date>=$today)),
                (SELECT COUNT(DISTINCT st.id) FROM school_staff st
                 JOIN employee_identities e ON e.id=st.id
                 WHERE e.original_payroll_school_id=s.id AND st.is_active=1
                   AND st.category='معلم'),
                (SELECT COUNT(DISTINCT st.id) FROM school_staff st
                 JOIN employee_assignments a ON a.employee_id=st.id
                 WHERE a.workplace_school_id=s.id AND st.is_active=1
                   AND st.category='معلم' AND a.assignment_type<>'إجازة'
                   AND (a.start_date IS NOT NULL OR
                        a.notes NOT IN ('سجل أصلي','سجل أصلي مرحل') OR
                        EXISTS(SELECT 1 FROM staff_personnel_details p
                               WHERE p.staff_id=st.id AND p.assignment_status='مباشر'))
                   AND (a.start_date IS NULL OR a.start_date<=$today)
                   AND (a.end_date IS NULL OR a.end_date>=$today)),
                (SELECT COUNT(*) FROM school_council_members c
                 WHERE c.school_id=s.id AND c.is_active=1)
            FROM schools s WHERE s.id=$school
            """, ("$school", schoolId), ("$year", year), ("$today", today),
            ("$current", currentYear ? 1 : 0));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new ArgumentException("المدرسة غير موجودة.", nameof(schoolId));
        return new SchoolOperationalSnapshot(schoolId, Text(reader, 0), year,
            Count64(reader, 1), Count64(reader, 2), Count64(reader, 3),
            Count64(reader, 4), Count64(reader, 5), Count64(reader, 6),
            Count64(reader, 7), Count64(reader, 8), Count64(reader, 9));
    }

    /// <summary>Live, no materialized cache. Correct after adds, edits, moves, archives and restores.</summary>
    public SchoolOperationalSnapshot GetSchoolOperationalSnapshot(
        UserAccount actor, string schoolId, string academicYear)
    {
        RequireFreshActor(actor);
        RequireStatisticsAccess(actor, schoolId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        EnsureYearExists(con, tx, academicYear);
        string activeYear;
        using (var active = Cmd(con, tx, "SELECT value FROM settings WHERE key='academic_year'"))
            activeYear = active.ExecuteScalar()?.ToString() ?? "";
        var result = ReadOperationalSnapshot(con, tx, schoolId, academicYear,
            DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            activeYear == academicYear);
        tx.Commit();
        return result;
    }

    private static IReadOnlyDictionary<int, (int Total, int Boys, int Girls)> ReadGradeTotals(
        SqliteConnection con, SqliteTransaction tx, string schoolId, string year, bool currentYear)
    {
        using var cmd = Cmd(con, tx, """
            SELECT g.grade,COUNT(DISTINCT h.student_id),
                   COUNT(DISTINCT CASE WHEN st.gender='ذكور' THEN h.student_id END),
                   COUNT(DISTINCT CASE WHEN st.gender='إناث' THEN h.student_id END)
            FROM school_grades g
            LEFT JOIN student_history h ON h.grade_id=g.id AND h.school_id=g.school_id
                AND h.academic_year=g.academic_year AND h.status='مسجل' AND h.end_utc IS NULL
            LEFT JOIN school_students st ON st.id=h.student_id
                AND (st.is_active=1 OR $current=0)
            WHERE g.school_id=$s AND g.academic_year=$y
            GROUP BY g.grade
            """, ("$s", schoolId), ("$y", year),
            ("$current", currentYear ? 1 : 0));
        var map = new Dictionary<int, (int, int, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // Only active school_students count; no retained archived student must
            // be counted via an otherwise still-active placement.
            var grade = r.GetInt32(0);
            map[grade] = (0, Count64(r, 2), Count64(r, 3));
            map[grade] = (map[grade].Item2 + map[grade].Item3,
                map[grade].Item2, map[grade].Item3);
        }
        return map;
    }

    private static int CountIntegrity(SqliteConnection con, SqliteTransaction tx,
        string schoolId, string sql)
    {
        using var cmd = Cmd(con, tx, sql, ("$school", schoolId));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountYearIntegrity(SqliteConnection con, SqliteTransaction tx,
        string schoolId, string academicYear, string sql)
    {
        using var cmd = Cmd(con, tx, sql,
            ("$school", schoolId), ("$year", academicYear));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// All schools are compared within one SQLite read transaction. Missing approved
    /// census is NULL, not zero. Past years retain student and section history, but
    /// current staffing is not misrepresented as a historical staffing snapshot.
    /// </summary>
    public IReadOnlyList<CensusReconciliationRow> GetCensusReconciliation(
        UserAccount actor, string academicYear)
    {
        RequireFreshActor(actor);
        RequireStatisticsAccess(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewReports))
            throw new UnauthorizedAccessException("تتطلب المطابقة صلاحية التقارير.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        EnsureYearExists(con, tx, academicYear);

        var schools = new List<string>();
        using (var schoolCmd = Cmd(con, tx, """
            SELECT id FROM schools WHERE $scope IS NULL OR id=$scope
            ORDER BY name,id
            """, ("$scope", actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId ?? "" : null)))
        using (var r = schoolCmd.ExecuteReader())
            while (r.Read()) schools.Add(Text(r, 0));

        // Latest APPROVED revision per school and round. Later drafts do not
        // silently overwrite or erase the official approved snapshot.
        var approved = new Dictionary<string, (string Id, int Students, int Boys, int Girls,
            int Teachers, int Sections)>();
        using (var official = Cmd(con, tx, """
            SELECT s.school_id,s.id,
                   COALESCE((SELECT SUM(e.boys+e.girls) FROM enrollment_rows e
                             WHERE e.submission_id=s.id),0),
                   COALESCE((SELECT SUM(e.boys) FROM enrollment_rows e
                             WHERE e.submission_id=s.id),0),
                   COALESCE((SELECT SUM(e.girls) FROM enrollment_rows e
                             WHERE e.submission_id=s.id),0),
                   s.male_teachers+s.female_teachers,s.classrooms
            FROM submissions s JOIN census_rounds r ON r.id=s.round_id
            WHERE r.academic_year=$year AND s.status='approved'
              AND NOT EXISTS (
                  SELECT 1 FROM submissions newer
                  WHERE newer.school_id=s.school_id AND newer.round_id=s.round_id
                    AND newer.status='approved' AND newer.revision>s.revision)
            """, ("$year", academicYear)))
        using (var r = official.ExecuteReader())
            while (r.Read())
                approved[Text(r, 0)] = (Text(r, 1), Count64(r, 2),
                    Count64(r, 3), Count64(r, 4), Count64(r, 5), Count64(r, 6));

        string currentAcademicYear;
        using (var current = Cmd(con, tx, "SELECT value FROM settings WHERE key='academic_year'"))
            currentAcademicYear = current.ExecuteScalar()?.ToString() ?? "";
        var isCurrent = academicYear == currentAcademicYear;
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var results = new List<CensusReconciliationRow>();
        foreach (var schoolId in schools)
        {
            var live = ReadOperationalSnapshot(con, tx, schoolId, academicYear, today, isCurrent);
            var hasOfficial = approved.TryGetValue(schoolId, out var official);
            string? id = hasOfficial ? official.Id : null;
            var context = isCurrent
                ? "القيم التشغيلية الحالية قد تختلف عن تاريخ لقطة الإحصاء المعتمدة."
                : "سجل سنة تاريخية؛ لا تفترض أن بيانات الوظيفة الحالية تمثل الكادر التاريخي.";

            void Add(string key, string label, int actual, int? reported, string note)
            {
                var definition = EducationalMetricCatalog.Find(key);
                results.Add(new CensusReconciliationRow(schoolId, live.SchoolName, academicYear,
                    key, label, actual, reported, definition?.Source ?? "student_history + school_grades",
                    definition?.Formula ?? "COUNT(DISTINCT active student IDs by grade)",
                    id, note));
            }

            Add("students.total", "الطلاب المسجلون", live.ActiveStudents,
                hasOfficial ? official.Students : null, context);
            Add("students.boys", "الطلاب الذكور", live.Boys,
                hasOfficial ? official.Boys : null, context);
            Add("students.girls", "الطالبات", live.Girls,
                hasOfficial ? official.Girls : null, context);
            Add("sections.total", "الشعب التعليمية وليست غرف المبنى", live.Sections,
                hasOfficial ? official.Sections : null, context);
            Add("teachers.workplace", "المعلمون العاملون فعليا", live.WorkplaceTeachers,
                hasOfficial && isCurrent ? official.Teachers : null, context);
            Add("teachers.payroll", "المعلمون على الملاك", live.PayrollTeachers, null,
                "مؤشر مستقل عن مكان العمل، ولا يساوي بالضرورة العدد المبلغ في الإحصاء.");
            Add("staff.payroll", "جميع الموظفين على الملاك", live.PayrollStaff, null,
                "مؤشر وظيفي مباشر؛ الموظف المنتدب لا يسجل مرتين.");
            Add("staff.workplace", "جميع الموظفين في مكان العمل", live.WorkplaceStaff, null,
                "مؤشر عمل فعلي محسوب حسب التكليف الساري بتاريخ اليوم.");
            Add("councils.active", "أعضاء مجالس أولياء الأمور", live.CouncilMembers, null,
                "من ملف المدرسة مباشرة دون نسخ بيانات الأعضاء إلى الاستمارة.");

            // Grade-level discrepancies expose errors hidden by district totals.
            var actualGrades = ReadGradeTotals(con, tx, schoolId, academicYear, isCurrent);
            var censusGrades = new Dictionary<int, int>();
            if (hasOfficial)
            {
                using var gradeCmd = Cmd(con, tx, """
                    SELECT grade,boys+girls FROM enrollment_rows WHERE submission_id=$id
                    """, ("$id", official.Id));
                using var gradeReader = gradeCmd.ExecuteReader();
                while (gradeReader.Read())
                    if (int.TryParse(Text(gradeReader, 0), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var grade))
                        censusGrades[grade] = Count64(gradeReader, 1);
            }
            foreach (var grade in actualGrades.Keys.Union(censusGrades.Keys).OrderBy(x => x))
            {
                var actual = actualGrades.TryGetValue(grade, out var data) ? data.Total : 0;
                Add("students.grade." + grade, "طلاب الصف " + grade, actual,
                    hasOfficial ? censusGrades.GetValueOrDefault(grade) : null,
                    "مقارنة كشف الصف المحسوب من القيد الحقيقي مع صف الاستمارة المعتمدة.");
            }

            // Detect bad foreign-key relationships, out-of-year grade placements,
            // repeated active enrollment, and false secondments. Never repair
            // identity or assignment history by guessing at its intended school.
            var orphanPlacements = CountYearIntegrity(con, tx, schoolId, academicYear, """
                SELECT COUNT(*) FROM student_history h
                LEFT JOIN school_students st ON st.id=h.student_id
                LEFT JOIN school_grades g ON g.id=h.grade_id
                    AND g.school_id=h.school_id AND g.academic_year=h.academic_year
                LEFT JOIN academic_years ay ON ay.year=h.academic_year
                WHERE h.school_id=$school AND h.academic_year=$year
                  AND (st.id IS NULL OR g.id IS NULL OR ay.year IS NULL)
                """);
            Add("integrity.student_reference", "قيود دراسية بمراجع غير صالحة",
                orphanPlacements, 0, "المستهدف صفر؛ فحص مراجع الطالب والصف والسنة وليس قيمة Census معتمدة.");
            var duplicateActivePlacements = CountYearIntegrity(con, tx, schoolId, academicYear, """
                SELECT COUNT(*) FROM (
                    SELECT h.student_id FROM student_history h
                    WHERE h.academic_year=$year AND h.status='مسجل' AND h.end_utc IS NULL
                    GROUP BY h.student_id HAVING COUNT(*)>1
                ) broken WHERE EXISTS (
                    SELECT 1 FROM student_history h
                    WHERE h.student_id=broken.student_id AND h.school_id=$school
                      AND h.academic_year=$year AND h.status='مسجل' AND h.end_utc IS NULL)
                """);
            Add("integrity.student_active_duplicate", "طلاب بقيود نشطة مكررة",
                duplicateActivePlacements, 0,
                "المستهدف صفر؛ يحمي الإحصاء من مضاعفة الطالب أثناء النقل أو الترفيع.");
            var invalidAssignments = CountIntegrity(con, tx, schoolId, """
                SELECT COUNT(*) FROM employee_assignments a
                LEFT JOIN employee_identities e ON e.id=a.employee_id
                LEFT JOIN schools work ON work.id=a.workplace_school_id
                LEFT JOIN schools payroll ON payroll.id=a.payroll_school_id
                WHERE (a.payroll_school_id=$school OR e.original_payroll_school_id=$school)
                  AND (e.id IS NULL OR payroll.id IS NULL OR work.id IS NULL
                       OR a.payroll_school_id<>e.original_payroll_school_id
                       OR (a.start_date IS NOT NULL AND a.end_date IS NOT NULL
                           AND a.start_date>a.end_date)
                       OR EXISTS(
                          SELECT 1 FROM employee_assignments other
                          WHERE other.employee_id=a.employee_id AND other.id<>a.id
                            AND COALESCE(a.start_date,'0001-01-01')
                               <= COALESCE(other.end_date,'9999-12-31')
                            AND COALESCE(other.start_date,'0001-01-01')
                               <= COALESCE(a.end_date,'9999-12-31')))
                """);
            Add("integrity.staff_assignment", "تكليف غير صحيح أو متداخل",
                invalidAssignments, 0,
                "المستهدف صفر؛ لا يحذف النظام التكليف غير المتسق ولا ينقل هوية الموظف آليا.");

            // Legacy inconsistencies remain visible rather than being merged by name.
            var invalidIdentity = CountIntegrity(con, tx, schoolId, """
                SELECT COUNT(*) FROM school_staff st
                LEFT JOIN employee_identities e ON e.id=st.id
                WHERE st.school_id=$school AND
                    (e.id IS NULL OR e.original_payroll_school_id<>st.school_id)
                """);
            Add("integrity.staff_identity", "كادر بلا هوية/ملاك مطابق", invalidIdentity, 0,
                "فحص سلامة مرجعية؛ المستهدف صفر ولا يعد رقما رسميا من الاستمارة.");
            var duplicateCodes = CountIntegrity(con, tx, schoolId, """
                SELECT COUNT(*) FROM school_staff st
                WHERE st.school_id=$school AND LENGTH(TRIM(st.employee_code))>0
                  AND EXISTS(SELECT 1 FROM school_staff other
                      WHERE other.school_id<>st.school_id
                        AND TRIM(other.employee_code)=TRIM(st.employee_code) COLLATE NOCASE)
                """);
            Add("integrity.staff_duplicate_code", "أرقام وظيفية متكررة بمدارس أخرى",
                duplicateCodes, 0, "لا تدمج الهويات المتشابهة تلقائيا؛ تتطلب تحقق بشري.");
        }
        tx.Commit();
        return results;
    }

    /// <summary>
    /// Explicit authorized acknowledgement of the CURRENT observed discrepancy.
    /// Appends an immutable audit record. Does not modify an approved submission.
    /// </summary>
    public void ReviewReconciliation(UserAccount actor, string schoolId, string academicYear,
        string metricKey, string explanation)
    {
        RequireFreshActor(actor);
        if (string.IsNullOrWhiteSpace(explanation) || explanation.Trim().Length > 1000)
            throw new ArgumentException("يجب إدخال سبب مراجعة واضح لا يتجاوز 1000 حرف.", nameof(explanation));
        if (!AccessPolicy.CanSeeSchool(actor, schoolId) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewReports) ||
            !AccessPolicy.Has(actor, PermissionFlags.ReviewCensus) ||
            actor.Role is not (Roles.Admin or Roles.Statistician or Roles.Reviewer))
            throw new UnauthorizedAccessException("تسجيل المراجعة للمخولين بمراجعة الإحصاء فقط.");
        var current = GetCensusReconciliation(actor, academicYear)
            .SingleOrDefault(x => x.SchoolId == schoolId && x.MetricKey == metricKey)
            ?? throw new InvalidOperationException("مؤشر المطابقة غير موجود في المدرسة والسنة المحددتين.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        Audit(con, tx, actor.Id, "census.reconciliation.review", schoolId,
            $"year={academicYear};metric={metricKey};operational={current.OperationalValue};" +
            $"approved={current.ApprovedValue?.ToString(CultureInfo.InvariantCulture) ?? "missing"};" +
            $"submission={current.SubmissionId ?? "none"};reason={explanation.Trim()}");
        tx.Commit();
    }
}
