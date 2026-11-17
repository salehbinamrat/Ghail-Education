using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>
/// Additive student subsystem. Student identifiers never cross a school boundary;
/// enrollment remains year-specific so moving to another class preserves historical results.
/// </summary>
public sealed partial class SqliteStore
{
    private static void MigrateStudentRecords(SqliteConnection con)
    {
        using var cmd = Cmd(con, null, """
            CREATE TABLE IF NOT EXISTS school_grades (
                id TEXT PRIMARY KEY,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                academic_year TEXT NOT NULL,
                grade INTEGER NOT NULL CHECK(grade BETWEEN 1 AND 12),
                section TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                UNIQUE(school_id,academic_year,grade,section),
                UNIQUE(id,school_id,academic_year)
            );
            CREATE INDEX IF NOT EXISTS ix_school_grades_year ON school_grades(school_id,academic_year,grade);
            CREATE TABLE IF NOT EXISTS school_students (
                id TEXT PRIMARY KEY,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                student_code TEXT NOT NULL COLLATE NOCASE,
                full_name TEXT NOT NULL,
                gender TEXT NOT NULL CHECK(gender IN ('ذكور','إناث')),
                guardian_name TEXT NOT NULL DEFAULT '',
                guardian_phone TEXT NOT NULL DEFAULT '',
                notes TEXT NOT NULL DEFAULT '',
                is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                UNIQUE(school_id,student_code),
                UNIQUE(id,school_id)
            );
            CREATE INDEX IF NOT EXISTS ix_student_school_name ON school_students(school_id,full_name);
            CREATE TABLE IF NOT EXISTS student_enrollments (
                student_id TEXT NOT NULL,
                school_id TEXT NOT NULL,
                academic_year TEXT NOT NULL,
                grade_id TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'مسجل',
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(student_id,academic_year),
                FOREIGN KEY(student_id,school_id) REFERENCES school_students(id,school_id) ON DELETE RESTRICT,
                FOREIGN KEY(grade_id,school_id,academic_year)
                    REFERENCES school_grades(id,school_id,academic_year) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_student_roster ON student_enrollments(school_id,academic_year,grade_id);
            CREATE TABLE IF NOT EXISTS student_results (
                id TEXT PRIMARY KEY,
                student_id TEXT NOT NULL,
                school_id TEXT NOT NULL,
                academic_year TEXT NOT NULL,
                term TEXT NOT NULL,
                subject TEXT NOT NULL COLLATE NOCASE,
                score INTEGER NOT NULL CHECK(score >= 0),
                maximum INTEGER NOT NULL CHECK(maximum > 0 AND score <= maximum),
                notes TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL,
                UNIQUE(student_id,academic_year,term,subject),
                FOREIGN KEY(student_id,school_id) REFERENCES school_students(id,school_id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_results_student_year ON student_results(student_id,academic_year);
            CREATE TABLE IF NOT EXISTS student_assessments (
                id TEXT PRIMARY KEY,
                student_id TEXT NOT NULL,
                school_id TEXT NOT NULL,
                academic_year TEXT NOT NULL,
                category TEXT NOT NULL,
                rating TEXT NOT NULL,
                notes TEXT NOT NULL DEFAULT '',
                recorded_utc TEXT NOT NULL,
                FOREIGN KEY(student_id,school_id) REFERENCES school_students(id,school_id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_assessments_student_date ON student_assessments(student_id,recorded_utc DESC);
            CREATE TABLE IF NOT EXISTS student_attendance (
                id TEXT PRIMARY KEY,
                student_id TEXT NOT NULL,
                school_id TEXT NOT NULL,
                attendance_date TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('حاضر','غائب','متأخر','بعذر')),
                notes TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL,
                UNIQUE(student_id,attendance_date),
                FOREIGN KEY(student_id,school_id) REFERENCES school_students(id,school_id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_attendance_student_day ON student_attendance(student_id,attendance_date DESC);
            """);
        cmd.ExecuteNonQuery();
    }

    private sealed record StudentSchoolInfo(string Code, string Stage, string Gender, bool IsActive);

    private static StudentSchoolInfo StudentSchool(SqliteConnection con, SqliteTransaction? tx, string schoolId)
    {
        using var cmd = Cmd(con, tx, "SELECT code,CASE WHEN is_kindergarten=1 THEN 'روضة' WHEN stage='أساسي وثانوي' THEN 'مجمع تربوي' ELSE stage END,gender,is_active FROM schools WHERE id=$s",
            ("$s", schoolId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(3) != 1)
            throw new InvalidOperationException("المدرسة غير موجودة أو غير نشطة.");
        return new StudentSchoolInfo(Text(reader, 0), Text(reader, 1), Text(reader, 2), true);
    }

    private static void EnsureSchoolStructureCompatible(SqliteConnection con, SqliteTransaction tx,
        string schoolId, string stage, string gender)
    {
        // A school cannot silently invalidate existing student genders or active class rosters.
        if (gender != "مختلطة")
        {
            using var students = Cmd(con, tx, """
                SELECT EXISTS(SELECT 1 FROM school_students
                  WHERE school_id=$s AND gender<>$gender)
                """, ("$s", schoolId), ("$gender", gender));
            if (Convert.ToInt32(students.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                throw new InvalidOperationException(
                    "تعذر تغيير نوع المدرسة؛ توجد ملفات طلاب لا تتوافق مع النوع الجديد.");
        }
        if (stage is "أساسي" or "ثانوي")
        {
            using var classes = Cmd(con, tx, """
                SELECT EXISTS(SELECT 1 FROM school_grades
                  WHERE school_id=$s AND (($stage='أساسي' AND grade>9)
                    OR ($stage='ثانوي' AND grade<10)))
                """, ("$s", schoolId), ("$stage", stage));
            if (Convert.ToInt32(classes.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                throw new InvalidOperationException(
                    "تعذر تغيير المرحلة؛ توجد صفوف دراسية مسجلة خارج نطاق المرحلة الجديدة.");
        }
    }

    private static void RequireEnrolledYear(SqliteConnection con, SqliteTransaction tx,
        string schoolId, string studentId, string year)
    {
        using var cmd = Cmd(con, tx, """
            SELECT EXISTS(SELECT 1 FROM student_enrollments en
              JOIN school_students st ON st.id=en.student_id AND st.school_id=en.school_id
              JOIN schools sc ON sc.id=en.school_id
              WHERE en.school_id=$s AND en.student_id=$student
                AND en.academic_year=$year AND en.status='مسجل'
                AND st.is_active=1 AND sc.is_active=1)
            """, ("$s", schoolId), ("$student", studentId), ("$year", year));
        if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException(
                "الطالب غير مسجل في العام الدراسي الحالي؛ لا يمكن إضافة نتيجة أو تقييم أو حضور جديد.");
    }

    private static string SectionName(string section)
    {
        section = section.Trim();
        if (section.Length is < 1 or > 20 ||
            section.Any(c => !(char.IsLetterOrDigit(c) || c is ' ' or '-')))
            throw new ArgumentException("اسم الشعبة يجب أن يتكون من أحرف أو أرقام أو شرطة فقط (حتى 20).");
        return section;
    }

    private static void GradeAllowed(StudentSchoolInfo school, int grade)
    {
        if (!GradeNames.IsAllowed(school.Stage, grade))
            throw new ArgumentException("الصف الدراسي غير متوافق مع مرحلة المدرسة.");
    }

    private static string StudentCode(string code)
    {
        code = code.Trim();
        if (code.Length is < 1 or > 40 ||
            code.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' or '-')))
            throw new ArgumentException("رمز الطالب مطلوب بحروف وأرقام إنجليزية فقط (ويُسمح - و _).");
        return code;
    }

    private static string StudentGender(StudentSchoolInfo school, string entered)
    {
        entered = entered.Trim();
        if (school.Gender != "مختلطة")
        {
            if (entered.Length != 0 && entered != school.Gender)
                throw new ArgumentException("جنس الطالب لا يتوافق مع نوع المدرسة.");
            return school.Gender;
        }
        if (entered is not ("ذكور" or "إناث"))
            throw new ArgumentException("حدد جنس الطالب في المدرسة المختلطة.");
        return entered;
    }

    private static void ValidateStudent(string name, string guardianName, string phone, string notes)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 ||
            guardianName.Length > 180 || phone.Length > 30 || notes.Length > 2000 ||
            phone.Any(c => !(char.IsAsciiDigit(c) || c is '+' or '-' or ' ')))
            throw new ArgumentException("بيانات الطالب أو ولي الأمر غير صالحة؛ راجع الاسم والهاتف والملاحظات.");
    }

    private static string GradeId(SqliteConnection con, SqliteTransaction tx,
        string schoolId, string year, int grade, string section)
    {
        using var cmd = Cmd(con, tx, """
            SELECT id FROM school_grades
            WHERE school_id=$s AND academic_year=$y AND grade=$g AND section=$section
            """, ("$s", schoolId), ("$y", year), ("$g", grade), ("$section", section));
        return cmd.ExecuteScalar()?.ToString()
            ?? throw new ArgumentException($"أضف الصف {grade} / الشعبة {section} أولا قبل حفظ الطالب.");
    }

    private static void StudentBelongs(SqliteConnection con, SqliteTransaction? tx,
        string schoolId, string studentId)
    {
        using var cmd = Cmd(con, tx,
            "SELECT EXISTS(SELECT 1 FROM school_students WHERE id=$id AND school_id=$s)",
            ("$id", studentId), ("$s", schoolId));
        if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new UnauthorizedAccessException("الطالب غير موجود ضمن المدرسة المحددة.");
    }

    public SchoolGrade AddSchoolGrade(UserAccount actor, string schoolId, int grade, string section)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudents(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية إنشاء الصفوف في هذه المدرسة.");
        section = SectionName(section);
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        var school = StudentSchool(con, tx, schoolId);
        GradeAllowed(school, grade);
        var id = Id();
        using (var cmd = Cmd(con, tx, """
            INSERT INTO school_grades(id,school_id,academic_year,grade,section,created_utc)
            VALUES($id,$s,$y,$g,$section,$at)
            ON CONFLICT(school_id,academic_year,grade,section) DO NOTHING
            """, ("$id", id), ("$s", schoolId), ("$y", year), ("$g", grade),
            ("$section", section), ("$at", Utc())))
        {
            if (cmd.ExecuteNonQuery() == 1)
                Audit(con, tx, actor.Id, "student.grade_create", id, $"school={schoolId};grade={grade}");
        }
        using var read = Cmd(con, tx, """
            SELECT id FROM school_grades
            WHERE school_id=$s AND academic_year=$y AND grade=$g AND section=$section
            """, ("$s", schoolId), ("$y", year), ("$g", grade), ("$section", section));
        id = read.ExecuteScalar()?.ToString()
            ?? throw new InvalidOperationException("تعذر إنشاء صف المدرسة.");
        tx.Commit();
        return new SchoolGrade(id, schoolId, year, grade, section, school.Stage);
    }

    /// <summary>
    /// Corrects a class or section in the current academic year in place. The stable
    /// class ID and its student enrollments remain intact; old academic years are not altered.
    /// </summary>
    public void UpdateSchoolGrade(UserAccount actor, string schoolId, string gradeId,
        int grade, string section)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudents(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية تعديل صفوف هذه المدرسة.");
        section = SectionName(section);
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        GradeAllowed(StudentSchool(con, tx, schoolId), grade);
        using var exists = Cmd(con, tx, """
            SELECT EXISTS(SELECT 1 FROM school_grades
            WHERE id=$id AND school_id=$s AND academic_year=$y)
            """, ("$id", gradeId), ("$s", schoolId), ("$y", year));
        if (Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new UnauthorizedAccessException("الصف غير موجود في المدرسة أو العام الدراسي المحدد.");
        using var duplicates = Cmd(con, tx, """
            SELECT EXISTS(SELECT 1 FROM school_grades
            WHERE school_id=$s AND academic_year=$y AND grade=$grade AND section=$section AND id<>$id)
            """, ("$id", gradeId), ("$s", schoolId), ("$y", year),
            ("$grade", grade), ("$section", section));
        if (Convert.ToInt32(duplicates.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
            throw new InvalidOperationException("يوجد كشف آخر بنفس الصف والشعبة؛ اختر شعبة مختلفة.");
        using var update = Cmd(con, tx, """
            UPDATE school_grades SET grade=$grade,section=$section
            WHERE id=$id AND school_id=$s AND academic_year=$y
            """, ("$grade", grade), ("$section", section), ("$id", gradeId),
            ("$s", schoolId), ("$y", year));
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("تعذر تصحيح الصف والشعبة.");
        Audit(con, tx, actor.Id, "student.grade_update", gradeId,
            $"school={schoolId};year={year};grade={grade};section={section}");
        tx.Commit();
    }

    public IReadOnlyList<SchoolGrade> GetSchoolGrades(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض صفوف المدرسة.");
        var year = AcademicYear;
        using var con = Open();
        var school = StudentSchool(con, null, schoolId);
        using var cmd = Cmd(con, null, """
            SELECT id,school_id,academic_year,grade,section
            FROM school_grades WHERE school_id=$s AND academic_year=$y
            ORDER BY grade,section
            """, ("$s", schoolId), ("$y", year));
        using var r = cmd.ExecuteReader();
        var result = new List<SchoolGrade>();
        while (r.Read()) result.Add(new SchoolGrade(Text(r, 0), Text(r, 1), Text(r, 2),
            r.GetInt32(3), Text(r, 4), school.Stage));
        return result;
    }

    private static SchoolStudent ReadStudent(SqliteDataReader r) =>
        new(Text(r, 0), Text(r, 1), Text(r, 2), Text(r, 3), r.GetInt32(4),
            Text(r, 5), Text(r, 6), Text(r, 7), Text(r, 8), Text(r, 9),
            r.GetInt32(10) == 1, Text(r, 11));

    public IReadOnlyList<SchoolStudent> GetStudents(UserAccount actor, string schoolId,
        string? gradeId = null)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض كشف طلاب هذه المدرسة.");
        var year = AcademicYear;
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT st.id,st.school_id,st.student_code,st.full_name,g.grade,g.section,
                st.gender,st.guardian_name,st.guardian_phone,st.notes,st.is_active,en.academic_year
            FROM school_students st
            JOIN student_enrollments en ON en.student_id=st.id AND en.school_id=st.school_id
            JOIN school_grades g ON g.id=en.grade_id AND g.school_id=en.school_id
                AND g.academic_year=en.academic_year
            WHERE st.school_id=$s AND en.academic_year=$y
                AND ($class IS NULL OR en.grade_id=$class)
            ORDER BY g.grade,g.section,st.full_name,st.student_code
            """, ("$s", schoolId), ("$y", year), ("$class", gradeId));
        using var r = cmd.ExecuteReader();
        var items = new List<SchoolStudent>();
        while (r.Read()) items.Add(ReadStudent(r));
        return items;
    }

    public SchoolStudent GetStudent(UserAccount actor, string schoolId, string studentId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض ملف الطالب.");
        var year = AcademicYear;
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT st.id,st.school_id,st.student_code,st.full_name,g.grade,g.section,
                st.gender,st.guardian_name,st.guardian_phone,st.notes,st.is_active,en.academic_year
            FROM school_students st
            JOIN student_enrollments en ON en.student_id=st.id AND en.school_id=st.school_id
            JOIN school_grades g ON g.id=en.grade_id AND g.school_id=en.school_id
                AND g.academic_year=en.academic_year
            WHERE st.id=$id AND st.school_id=$s AND en.academic_year=$year
            """, ("$id", studentId), ("$s", schoolId), ("$year", year));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new UnauthorizedAccessException("لا يمكن فتح الطالب خارج كشف المدرسة الحالي.");
        return ReadStudent(reader);
    }

    public void SaveStudent(UserAccount actor, SchoolStudent student)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudents(actor, student.SchoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية تعديل طلاب هذه المدرسة.");
        var code = StudentCode(student.StudentCode);
        var section = SectionName(student.Section);
        ValidateStudent(student.FullName, student.GuardianName, student.GuardianPhone, student.Notes);
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        var school = StudentSchool(con, tx, student.SchoolId);
        GradeAllowed(school, student.Grade);
        var gender = StudentGender(school, student.Gender);
        var gradeId = GradeId(con, tx, student.SchoolId, year, student.Grade, section);
        var id = string.IsNullOrWhiteSpace(student.Id) ? Id() : student.Id;
        using (var duplicate = Cmd(con, tx, """
            SELECT EXISTS(SELECT 1 FROM school_students
              WHERE school_id=$s AND student_code=$code AND id<>$id)
            """, ("$s", student.SchoolId), ("$code", code), ("$id", id)))
        {
            if (Convert.ToInt32(duplicate.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new ArgumentException("رمز الطالب موجود بالفعل في هذه المدرسة.");
        }
        if (string.IsNullOrWhiteSpace(student.Id))
        {
            using var add = Cmd(con, tx, """
                INSERT INTO school_students(id,school_id,student_code,full_name,gender,
                    guardian_name,guardian_phone,notes,is_active,created_utc,updated_utc)
                VALUES($id,$s,$code,$name,$gender,$guardian,$phone,$notes,$active,$at,$at)
                """, ("$id", id), ("$s", student.SchoolId), ("$code", code),
                ("$name", student.FullName.Trim()), ("$gender", gender),
                ("$guardian", student.GuardianName.Trim()), ("$phone", student.GuardianPhone.Trim()),
                ("$notes", student.Notes.Trim()), ("$active", student.IsActive ? 1 : 0),
                ("$at", Utc()));
            add.ExecuteNonQuery();
        }
        else
        {
            using var update = Cmd(con, tx, """
                UPDATE school_students
                SET student_code=$code,full_name=$name,gender=$gender,guardian_name=$guardian,
                    guardian_phone=$phone,notes=$notes,is_active=$active,updated_utc=$at
                WHERE id=$id AND school_id=$s
                """, ("$id", id), ("$s", student.SchoolId), ("$code", code),
                ("$name", student.FullName.Trim()), ("$gender", gender),
                ("$guardian", student.GuardianName.Trim()), ("$phone", student.GuardianPhone.Trim()),
                ("$notes", student.Notes.Trim()), ("$active", student.IsActive ? 1 : 0),
                ("$at", Utc()));
            if (update.ExecuteNonQuery() != 1)
                throw new UnauthorizedAccessException("لا يمكن نقل طالب إلى مدرسة أخرى بهذه العملية.");
        }
        using (var enroll = Cmd(con, tx, """
            INSERT INTO student_enrollments(student_id,school_id,academic_year,grade_id,status,updated_utc)
            VALUES($id,$s,$y,$g,'مسجل',$at)
            ON CONFLICT(student_id,academic_year) DO UPDATE
            SET grade_id=excluded.grade_id,updated_utc=excluded.updated_utc
            """, ("$id", id), ("$s", student.SchoolId), ("$y", year), ("$g", gradeId), ("$at", Utc())))
            enroll.ExecuteNonQuery();
        Audit(con, tx, actor.Id, string.IsNullOrWhiteSpace(student.Id)
                ? "student.create" : "student.update", id, $"school={student.SchoolId};grade={student.Grade}");
        tx.Commit();
    }

    private static void ValidateNarrative(string? notes, int limit = 1000)
    {
        if (notes is null || notes.Length > limit)
            throw new ArgumentException("الملاحظات تتجاوز الحد المسموح.");
    }

    public IReadOnlyList<StudentResult> GetStudentResults(UserAccount actor, string schoolId, string studentId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException();
        using var con = Open();
        StudentBelongs(con, null, schoolId, studentId);
        using var cmd = Cmd(con, null, """
            SELECT id,student_id,academic_year,term,subject,score,maximum,notes
            FROM student_results WHERE school_id=$s AND student_id=$id
            ORDER BY academic_year DESC,term,subject
            """, ("$s", schoolId), ("$id", studentId));
        using var r = cmd.ExecuteReader();
        var records = new List<StudentResult>();
        while (r.Read()) records.Add(new StudentResult(Text(r, 0), Text(r, 1), Text(r, 2),
            Text(r, 3), Text(r, 4), r.GetInt32(5), r.GetInt32(6), Text(r, 7)));
        return records;
    }

    public void SaveStudentResult(UserAccount actor, string schoolId, string studentId,
        string term, string subject, int score, int maximum, string notes)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudentEvaluation(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية إدخال درجات الطلاب.");
        if (term is not ("الفصل الأول" or "الفصل الثاني" or "النهائي") ||
            string.IsNullOrWhiteSpace(subject) || subject.Length > 100 ||
            maximum is < 1 or > 1000 || score < 0 || score > maximum)
            throw new ArgumentException("اسم المادة أو الفصل أو الدرجة غير صحيح.");
        ValidateNarrative(notes);
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        StudentBelongs(con, tx, schoolId, studentId);
        RequireEnrolledYear(con, tx, schoolId, studentId, year);
        using var cmd = Cmd(con, tx, """
            INSERT INTO student_results(id,student_id,school_id,academic_year,term,
                subject,score,maximum,notes,updated_utc)
            VALUES($rid,$student,$s,$year,$term,$subject,$score,$max,$notes,$at)
            ON CONFLICT(student_id,academic_year,term,subject) DO UPDATE SET
                score=excluded.score,maximum=excluded.maximum,
                notes=excluded.notes,updated_utc=excluded.updated_utc
            """, ("$rid", Id()), ("$student", studentId), ("$s", schoolId),
            ("$year", year), ("$term", term), ("$subject", subject.Trim()),
            ("$score", score), ("$max", maximum), ("$notes", notes.Trim()), ("$at", Utc()));
        cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "student.result", studentId,
            $"school={schoolId};year={year};term={term}");
        tx.Commit();
    }

    public IReadOnlyList<StudentAssessment> GetStudentAssessments(UserAccount actor,
        string schoolId, string studentId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException();
        using var con = Open();
        StudentBelongs(con, null, schoolId, studentId);
        using var cmd = Cmd(con, null, """
            SELECT id,student_id,academic_year,category,rating,notes,recorded_utc
            FROM student_assessments WHERE school_id=$s AND student_id=$id
            ORDER BY recorded_utc DESC LIMIT 500
            """, ("$s", schoolId), ("$id", studentId));
        using var r = cmd.ExecuteReader();
        var result = new List<StudentAssessment>();
        while (r.Read()) result.Add(new StudentAssessment(Text(r, 0), Text(r, 1), Text(r, 2),
            Text(r, 3), Text(r, 4), Text(r, 5),
            DateTimeOffset.Parse(Text(r, 6), CultureInfo.InvariantCulture)));
        return result;
    }

    public void AddStudentAssessment(UserAccount actor, string schoolId, string studentId,
        string category, string rating, string notes)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudentEvaluation(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية إضافة تقييمات الطالب.");
        if (category is not ("المشاركة" or "السلوك" or "المواظبة" or "المهارات" or "التقييم العام") ||
            rating is not ("ممتاز" or "جيد جدا" or "جيد" or "مقبول" or "يحتاج متابعة"))
            throw new ArgumentException("حدد فئة التقييم والتقدير من القيم المتاحة.");
        ValidateNarrative(notes);
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        StudentBelongs(con, tx, schoolId, studentId);
        RequireEnrolledYear(con, tx, schoolId, studentId, year);
        using var cmd = Cmd(con, tx, """
            INSERT INTO student_assessments(id,student_id,school_id,academic_year,
                category,rating,notes,recorded_utc)
            VALUES($id,$student,$s,$y,$category,$rating,$notes,$at)
            """, ("$id", Id()), ("$student", studentId), ("$s", schoolId),
            ("$y", year), ("$category", category), ("$rating", rating),
            ("$notes", notes.Trim()), ("$at", Utc()));
        cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "student.assessment", studentId, $"school={schoolId};category={category}");
        tx.Commit();
    }

    /// <summary>Correct one current-year evaluation without deleting its historical identity.</summary>
    public void UpdateStudentAssessment(UserAccount actor, string schoolId, string studentId,
        string assessmentId, string category, string rating, string notes)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudentEvaluation(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية تصحيح تقييمات الطلاب.");
        if (category is not ("المشاركة" or "السلوك" or "المواظبة" or "المهارات" or "التقييم العام") ||
            rating is not ("ممتاز" or "جيد جدا" or "جيد" or "مقبول" or "يحتاج متابعة"))
            throw new ArgumentException("اختر فئة التقييم والتقدير من القيم المتاحة.");
        ValidateNarrative(notes);
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        StudentBelongs(con, tx, schoolId, studentId);
        RequireEnrolledYear(con, tx, schoolId, studentId, year);
        using var cmd = Cmd(con, tx, """
            UPDATE student_assessments SET category=$category,rating=$rating,notes=$notes
            WHERE id=$id AND student_id=$student AND school_id=$s AND academic_year=$year
            """, ("$id", assessmentId), ("$student", studentId), ("$s", schoolId),
            ("$year", year), ("$category", category), ("$rating", rating),
            ("$notes", notes.Trim()));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("التقييم غير موجود أو يخص عاما دراسيا سابقا؛ لا يجوز تغييره من هنا.");
        Audit(con, tx, actor.Id, "student.assessment_correct", assessmentId,
            $"school={schoolId};student={studentId};year={year}");
        tx.Commit();
    }

    public IReadOnlyList<StudentAttendance> GetStudentAttendance(UserAccount actor,
        string schoolId, string studentId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException();
        using var con = Open();
        StudentBelongs(con, null, schoolId, studentId);
        using var cmd = Cmd(con, null, """
            SELECT id,student_id,attendance_date,status,notes
            FROM student_attendance WHERE school_id=$s AND student_id=$id
            ORDER BY attendance_date DESC LIMIT 1000
            """, ("$s", schoolId), ("$id", studentId));
        using var r = cmd.ExecuteReader();
        var result = new List<StudentAttendance>();
        while (r.Read()) result.Add(new StudentAttendance(Text(r, 0), Text(r, 1), Text(r, 2),
            Text(r, 3), Text(r, 4)));
        return result;
    }

    public void RecordStudentAttendance(UserAccount actor, string schoolId, string studentId,
        DateOnly date, string status, string notes)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudentEvaluation(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية تسجيل حضور الطلاب.");
        if (status is not ("حاضر" or "غائب" or "متأخر" or "بعذر"))
            throw new ArgumentException("حالة حضور الطالب غير صحيحة.");
        ValidateNarrative(notes, 400);
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        StudentBelongs(con, tx, schoolId, studentId);
        RequireEnrolledYear(con, tx, schoolId, studentId, year);
        using var cmd = Cmd(con, tx, """
            INSERT INTO student_attendance(id,student_id,school_id,attendance_date,
                status,notes,updated_utc)
            VALUES($id,$student,$s,$day,$status,$notes,$at)
            ON CONFLICT(student_id,attendance_date) DO UPDATE
            SET status=excluded.status,notes=excluded.notes,updated_utc=excluded.updated_utc
            """, ("$id", Id()), ("$student", studentId), ("$s", schoolId),
            ("$day", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$status", status), ("$notes", notes.Trim()), ("$at", Utc()));
        cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "student.attendance", studentId,
            $"school={schoolId};day={date:yyyy-MM-dd};status={status}");
        tx.Commit();
    }

    public int ImportStudents(UserAccount actor, string schoolId,
        IReadOnlyList<StudentImportRow> rows, string sourceName)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudents(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية استيراد طلاب هذه المدرسة.");
        if (rows.Count is < 1 or > 5000 || string.IsNullOrWhiteSpace(sourceName) ||
            Path.GetExtension(sourceName).ToLowerInvariant() is not (".xlsx" or ".docx"))
            throw new ArgumentException("ملف استيراد الطلاب غير صالح.");
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        var school = StudentSchool(con, tx, schoolId);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // One class/section identifier lookup per distinct class, not per imported student.
        var gradeIds = new Dictionary<(int Grade, string Section), string>();
        var ready = new List<(StudentImportRow Row, string Code, string Section, string Gender, string GradeId)>(rows.Count);
        foreach (var item in rows)
        {
            if (!string.IsNullOrWhiteSpace(item.SchoolCode) &&
                !string.Equals(item.SchoolCode.Trim(), school.Code, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("الملف يتضمن رمزا يخص مدرسة غير المحددة. توقف الاستيراد كاملا.");
            var code = StudentCode(item.StudentCode);
            if (!seen.Add(code))
                throw new ArgumentException("يوجد تكرار لرمز الطالب داخل ملف الاستيراد.");
            ValidateStudent(item.FullName, item.GuardianName, item.GuardianPhone, "");
            GradeAllowed(school, item.Grade);
            var section = SectionName(item.Section);
            var gender = StudentGender(school, item.Gender);
            if (!gradeIds.TryGetValue((item.Grade, section), out var gradeId))
            {
                gradeId = GradeId(con, tx, schoolId, year, item.Grade, section);
                gradeIds.Add((item.Grade, section), gradeId);
            }
            ready.Add((item, code, section, gender, gradeId));
        }

        using var upsert = Cmd(con, tx, """
            INSERT INTO school_students(id,school_id,student_code,full_name,gender,
                guardian_name,guardian_phone,notes,is_active,created_utc,updated_utc)
            VALUES($id,$s,$code,$name,$gender,$guardian,$phone,'',1,$at,$at)
            ON CONFLICT(school_id,student_code) DO UPDATE SET
                full_name=excluded.full_name,gender=excluded.gender,
                guardian_name=CASE WHEN excluded.guardian_name='' THEN school_students.guardian_name
                    ELSE excluded.guardian_name END,
                guardian_phone=CASE WHEN excluded.guardian_phone='' THEN school_students.guardian_phone
                    ELSE excluded.guardian_phone END,
                updated_utc=excluded.updated_utc
            RETURNING id
            """, ("$id", ""), ("$s", schoolId), ("$code", ""), ("$name", ""),
            ("$gender", ""), ("$guardian", ""), ("$phone", ""), ("$at", ""));
        upsert.Prepare();
        using var enrollment = Cmd(con, tx, """
            INSERT INTO student_enrollments(student_id,school_id,academic_year,grade_id,status,updated_utc)
            VALUES($student,$s,$year,$grade,'مسجل',$at)
            ON CONFLICT(student_id,academic_year) DO UPDATE
            SET grade_id=excluded.grade_id,updated_utc=excluded.updated_utc
            """, ("$student", ""), ("$s", schoolId), ("$year", year),
            ("$grade", ""), ("$at", ""));
        enrollment.Prepare();
        var at = Utc();
        foreach (var (item, code, _, gender, gradeId) in ready)
        {
            upsert.Parameters["$id"].Value = Id();
            upsert.Parameters["$code"].Value = code;
            upsert.Parameters["$name"].Value = item.FullName.Trim();
            upsert.Parameters["$gender"].Value = gender;
            upsert.Parameters["$guardian"].Value = item.GuardianName.Trim();
            upsert.Parameters["$phone"].Value = item.GuardianPhone.Trim();
            upsert.Parameters["$at"].Value = at;
            var studentId = upsert.ExecuteScalar()?.ToString()
                ?? throw new InvalidOperationException("تعذر الحصول على معرف الطالب بعد الاستيراد.");
            enrollment.Parameters["$student"].Value = studentId;
            enrollment.Parameters["$grade"].Value = gradeId;
            enrollment.Parameters["$at"].Value = at;
            enrollment.ExecuteNonQuery();
        }
        Audit(con, tx, actor.Id, "student.import", schoolId,
            $"school={schoolId};count={ready.Count};type={Path.GetExtension(sourceName).ToLowerInvariant()}");
        tx.Commit();
        return ready.Count;
    }
}
