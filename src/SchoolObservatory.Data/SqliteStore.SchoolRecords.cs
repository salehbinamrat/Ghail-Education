using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    /// <summary>Additive migration: never discard or rebuild the school's existing census and accounts.</summary>
    private static void MigrateSchoolRecords(SqliteConnection con)
    {
        using (var check = Cmd(con, null,
            "SELECT COUNT(*) FROM pragma_table_info('schools') WHERE name='gender'"))
        {
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using var alter = Cmd(con, null, """
                    ALTER TABLE schools ADD COLUMN gender TEXT NOT NULL DEFAULT 'مختلطة'
                    CHECK(gender IN ('ذكور','إناث','مختلطة'))
                    """);
                alter.ExecuteNonQuery();
            }
        }
        // Additive upgrade: the original schools.stage CHECK did not contain kindergarten.
        // Preserve all existing school IDs, foreign keys, census and account records.
        using (var checkKindergarten = Cmd(con, null,
            "SELECT COUNT(*) FROM pragma_table_info('schools') WHERE name='is_kindergarten'"))
        {
            if (Convert.ToInt32(checkKindergarten.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using var alter = Cmd(con, null, """
                    ALTER TABLE schools ADD COLUMN is_kindergarten INTEGER NOT NULL DEFAULT 0
                    CHECK(is_kindergarten IN (0,1))
                    """);
                alter.ExecuteNonQuery();
            }
        }
        using var cmd = Cmd(con, null, """
            CREATE TABLE IF NOT EXISTS school_staff (
                id TEXT PRIMARY KEY,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                employee_code TEXT NOT NULL COLLATE NOCASE,
                full_name TEXT NOT NULL,
                category TEXT NOT NULL CHECK(category IN ('معلم','إداري','أخصائي اجتماعي')),
                job_title TEXT NOT NULL DEFAULT '',
                specialization TEXT NOT NULL DEFAULT '',
                qualification TEXT NOT NULL DEFAULT '',
                gender TEXT NOT NULL CHECK(gender IN ('ذكور','إناث','غير محدد')),
                is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                UNIQUE(school_id,employee_code)
            );
            CREATE INDEX IF NOT EXISTS ix_staff_school_category ON school_staff(school_id,category,is_active);
            """);
        cmd.ExecuteNonQuery();
    }

    private static void CheckGender(string gender)
    {
        if (gender is not ("ذكور" or "إناث" or "مختلطة"))
            throw new ArgumentException("نوع المدرسة يجب أن يكون ذكور أو إناث أو مختلطة.");
    }

    private static string DatabaseStage(string stage)
    {
        if (stage is not ("روضة" or "أساسي" or "ثانوي" or "مجمع تربوي" or "أساسي وثانوي"))
            throw new ArgumentException("المرحلة يجب أن تكون روضة أو أساسي أو ثانوي أو مجمع تربوي.");
        return stage switch
        {
            "مجمع تربوي" => "أساسي وثانوي",
            "روضة" => "أساسي", // Original CHECK preserved; display relies on is_kindergarten.
            _ => stage
        };
    }

    private static string DisplayStage(string stage) =>
        stage == "أساسي وثانوي" ? "مجمع تربوي" : stage;

    private static void EnsureKindergartenTransitionCompatible(
        SqliteConnection con, SqliteTransaction tx, string schoolId, bool kindergarten)
    {
        using var current = Cmd(con, tx,
            "SELECT is_kindergarten FROM schools WHERE id=$s", ("$s", schoolId));
        var stored = current.ExecuteScalar();
        if (stored is null)
            throw new InvalidOperationException("المدرسة غير موجودة.");
        if ((Convert.ToInt32(stored, CultureInfo.InvariantCulture) == 1) == kindergarten)
            return;

        // The same grade number means something different in kindergarten and primary school.
        // Prevent silent reinterpretation of school records.
        using var records = Cmd(con, tx, """
            SELECT EXISTS(SELECT 1 FROM school_grades WHERE school_id=$s)
                OR EXISTS(SELECT 1 FROM school_students WHERE school_id=$s)
                OR EXISTS(SELECT 1 FROM submissions WHERE school_id=$s)
            """, ("$s", schoolId));
        if (Convert.ToInt32(records.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException(
                "لا يمكن تحويل المرحلة إلى روضة أو منها بعد تسجيل صفوف أو طلاب أو إحصاءات؛ حفاظا على السجلات.");
    }

    private static void ValidateStaff(SchoolStaff staff)
    {
        if (string.IsNullOrWhiteSpace(staff.SchoolId) ||
            string.IsNullOrWhiteSpace(staff.EmployeeCode) || staff.EmployeeCode.Length > 40 ||
            string.IsNullOrWhiteSpace(staff.FullName) || staff.FullName.Length > 180 ||
            staff.Category is not ("معلم" or "إداري" or "أخصائي اجتماعي") ||
            staff.Gender is not ("ذكور" or "إناث" or "غير محدد") ||
            staff.JobTitle.Length > 120 || staff.Specialization.Length > 120 ||
            staff.Qualification.Length > 120)
            throw new ArgumentException("سجل الموظف غير مكتمل أو يحتوي قيما غير مسموحة.");
    }

    private static SchoolStaff ReadStaff(SqliteDataReader reader) =>
        new(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3),
            Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7),
            Text(reader, 8), reader.GetInt32(9) == 1);

    public IReadOnlyList<SchoolStaff> GetSchoolStaff(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStaff(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية الاطلاع على كادر هذه المدرسة.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT id,school_id,employee_code,full_name,category,job_title,
                   specialization,qualification,gender,is_active
            FROM school_staff WHERE school_id=$s ORDER BY category, full_name
            """, ("$s", schoolId));
        using var r = cmd.ExecuteReader();
        var items = new List<SchoolStaff>();
        while (r.Read()) items.Add(ReadStaff(r));
        return items;
    }

    public void SaveSchoolStaff(UserAccount actor, SchoolStaff staff)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditStaff(actor, staff.SchoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية تعديل كادر هذه المدرسة.");
        ValidateStaff(staff);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var school = Cmd(con, tx, "SELECT is_active FROM schools WHERE id=$s", ("$s", staff.SchoolId)))
        {
            if (Convert.ToInt32(school.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("المدرسة غير نشطة أو غير موجودة.");
        }
        var newRecord = string.IsNullOrWhiteSpace(staff.Id);
        var id = newRecord ? Id() : staff.Id;
        if (newRecord)
        {
            using var create = Cmd(con, tx, """
                INSERT INTO school_staff(id,school_id,employee_code,full_name,category,job_title,
                    specialization,qualification,gender,is_active,created_utc,updated_utc)
                VALUES($id,$s,$code,$name,$category,$job,$subject,$qual,$gender,$active,$at,$at)
                """, ("$id", id), ("$s", staff.SchoolId),
                ("$code", staff.EmployeeCode.Trim()), ("$name", staff.FullName.Trim()),
                ("$category", staff.Category), ("$job", staff.JobTitle.Trim()),
                ("$subject", staff.Specialization.Trim()), ("$qual", staff.Qualification.Trim()),
                ("$gender", staff.Gender), ("$active", staff.IsActive ? 1 : 0),
                ("$at", Utc()));
            create.ExecuteNonQuery();
        }
        else
        {
            using var update = Cmd(con, tx, """
                UPDATE school_staff SET employee_code=$code,full_name=$name,category=$category,
                    job_title=$job,specialization=$subject,qualification=$qual,
                    gender=$gender,is_active=$active,updated_utc=$at
                WHERE id=$id AND school_id=$s
                """, ("$id", id), ("$s", staff.SchoolId), ("$code", staff.EmployeeCode.Trim()),
                ("$name", staff.FullName.Trim()), ("$category", staff.Category),
                ("$job", staff.JobTitle.Trim()), ("$subject", staff.Specialization.Trim()),
                ("$qual", staff.Qualification.Trim()), ("$gender", staff.Gender),
                ("$active", staff.IsActive ? 1 : 0), ("$at", Utc()));
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("لم يعثر على سجل الموظف ضمن المدرسة المحددة.");
        }
        Audit(con, tx, actor.Id, newRecord ? "staff.create" : "staff.update",
            id, $"school={staff.SchoolId};category={staff.Category}");
        tx.Commit();
    }

    /// <summary>School account can update its own classification, never code/name/other schools.</summary>
    public void UpdateOwnSchoolProfile(UserAccount actor, string schoolId, string stage, string gender)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditSchoolProfile(actor, schoolId))
            throw new UnauthorizedAccessException("لا يمكنك تعديل نوع أو مرحلة هذه المدرسة.");
        CheckGender(gender);
        var storedStage = DatabaseStage(stage);
        using var con = Open();
        using var tx = con.BeginTransaction();
        EnsureSchoolStructureCompatible(con, tx, schoolId, storedStage, gender);
        EnsureKindergartenTransitionCompatible(con, tx, schoolId, stage == "روضة");
        using (var cmd = Cmd(con, tx, """
            UPDATE schools SET stage=$stage,gender=$gender,is_kindergarten=$kindergarten
            WHERE id=$s AND is_active=1
            """, ("$s", schoolId), ("$stage", storedStage), ("$gender", gender),
                ("$kindergarten", stage == "روضة" ? 1 : 0)))
        {
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("المدرسة غير موجودة أو غير نشطة.");
        }
        Audit(con, tx, actor.Id, "school.profile", schoolId,
            $"stage={storedStage};gender={gender}");
        tx.Commit();
    }

    /// <summary>
    /// Match every staff row by school code, check the actor's school scope for the whole file,
    /// then upsert atomically. Never silently create schools from an unrecognized code.
    /// </summary>
    public int ImportStaffRecords(UserAccount actor, IReadOnlyList<StaffImportRow> rows, string sourceName)
    {
        RequireFreshActor(actor);
        if (rows.Count is < 1 or > 5000)
            throw new ArgumentException("يجب أن يحتوي الملف من 1 إلى 5000 سجل.");
        if (string.IsNullOrWhiteSpace(sourceName) || sourceName.Length > 250)
            throw new ArgumentException("اسم ملف الاستيراد غير صحيح.");
        var schools = GetSchools(actor).Where(x => x.IsActive)
            .ToDictionary(x => x.Code, x => x, StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prepared = new List<(School School, StaffImportRow Row)>(rows.Count);
        foreach (var item in rows)
        {
            var code = item.SchoolCode.Trim();
            // Never tell a school-scoped user whether a foreign school really exists.
            if ((actor.Role is Roles.Entry or Roles.Principal) &&
                !schools.ContainsKey(code))
                throw new UnauthorizedAccessException("الملف يحتوي سجلات خارج نطاق مدرستك.");
            if (!schools.TryGetValue(code, out var school))
                throw new ArgumentException($"رمز المدرسة غير موجود أو المدرسة غير نشطة: {code}");
            if (!AccessPolicy.CanEditStaff(actor, school.Id))
                throw new UnauthorizedAccessException("لا تملك صلاحية استيراد سجلات هذه المدرسة.");
            var staff = new SchoolStaff("", school.Id, item.EmployeeCode, item.FullName,
                item.Category, item.JobTitle, item.Specialization, item.Qualification,
                string.IsNullOrWhiteSpace(item.Gender) ? "غير محدد" : item.Gender, true);
            ValidateStaff(staff);
            if (!duplicates.Add(school.Id + "/" + item.EmployeeCode.Trim()))
                throw new ArgumentException("رقم موظف مكرر لنفس المدرسة داخل ملف الاستيراد.");
            prepared.Add((school, item));
        }

        using var con = Open();
        using var tx = con.BeginTransaction();
        using var insert = Cmd(con, tx, """
            INSERT INTO school_staff(id,school_id,employee_code,full_name,category,job_title,
                specialization,qualification,gender,is_active,created_utc,updated_utc)
            VALUES($id,$school,$code,$name,$category,$job,$subject,$qual,$gender,1,$at,$at)
            ON CONFLICT(school_id,employee_code) DO UPDATE SET
                full_name=excluded.full_name,category=excluded.category,
                job_title=excluded.job_title,specialization=excluded.specialization,
                qualification=excluded.qualification,gender=excluded.gender,
                updated_utc=excluded.updated_utc
            """, ("$id", ""), ("$school", ""), ("$code", ""), ("$name", ""),
            ("$category", ""), ("$job", ""), ("$subject", ""), ("$qual", ""),
            ("$gender", ""), ("$at", ""));
        insert.Prepare();
        var at = Utc();
        foreach (var (school, item) in prepared)
        {
            insert.Parameters["$id"].Value = Id();
            insert.Parameters["$school"].Value = school.Id;
            insert.Parameters["$code"].Value = item.EmployeeCode.Trim();
            insert.Parameters["$name"].Value = item.FullName.Trim();
            insert.Parameters["$category"].Value = item.Category;
            insert.Parameters["$job"].Value = item.JobTitle.Trim();
            insert.Parameters["$subject"].Value = item.Specialization.Trim();
            insert.Parameters["$qual"].Value = item.Qualification.Trim();
            insert.Parameters["$gender"].Value = string.IsNullOrWhiteSpace(item.Gender)
                ? "غير محدد" : item.Gender;
            insert.Parameters["$at"].Value = at;
            insert.ExecuteNonQuery();
        }
        Audit(con, tx, actor.Id, "staff.import", "school_staff",
            $"rows={rows.Count};schools={prepared.Select(x => x.School.Id).Distinct().Count()};format={Path.GetExtension(sourceName).ToLowerInvariant()}");
        tx.Commit();
        return rows.Count;
    }
}
