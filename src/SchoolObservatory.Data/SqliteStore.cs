using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>Local single-device SQLite persistence. Not encrypted: do not store sensitive real-world data yet.</summary>
public sealed partial class SqliteStore
{
    private readonly string _connectionString;

    public SqliteStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Database path is required", nameof(path));
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return con;
    }

    private static SqliteCommand Cmd(SqliteConnection con, SqliteTransaction? tx, string sql,
        params (string Key, object? Value)[] args)
    {
        var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }

    private static string Utc() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static string Id() => Guid.NewGuid().ToString("N");
    private static string Text(SqliteDataReader r, int i) => r.GetString(i);
    private static string? NullableText(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public void Initialize()
    {
        using var con = Open();
        using var pragma = Cmd(con, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        pragma.ExecuteNonQuery();
        using var cmd = Cmd(con, null, """
            CREATE TABLE IF NOT EXISTS settings (
              key TEXT PRIMARY KEY, value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS schools (
              id TEXT PRIMARY KEY, code TEXT NOT NULL COLLATE NOCASE UNIQUE, name TEXT NOT NULL,
              zone TEXT NOT NULL, ownership TEXT NOT NULL CHECK(ownership IN ('حكومية','أهلية')),
              stage TEXT NOT NULL CHECK(stage IN ('أساسي','ثانوي','أساسي وثانوي')),
              is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN(0,1)),
              created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS users (
              id TEXT PRIMARY KEY, username TEXT NOT NULL COLLATE NOCASE UNIQUE,
              password_hash TEXT NOT NULL,
              role TEXT NOT NULL CHECK(role IN ('admin','statistician','reviewer','principal','entry','viewer')),
              school_id TEXT NULL REFERENCES schools(id) ON DELETE RESTRICT,
              is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)),
              failed_attempts INTEGER NOT NULL DEFAULT 0,
              lock_until_utc TEXT NULL, created_utc TEXT NOT NULL,
              CHECK ((role IN ('entry','principal') AND school_id IS NOT NULL) OR
                     (role NOT IN ('entry','principal') AND school_id IS NULL))
            );
            CREATE TABLE IF NOT EXISTS census_rounds (
              id TEXT PRIMARY KEY, academic_year TEXT NOT NULL UNIQUE,
              snapshot_date TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS submissions (
              id TEXT PRIMARY KEY, school_id TEXT NOT NULL REFERENCES schools(id),
              round_id TEXT NOT NULL REFERENCES census_rounds(id),
              revision INTEGER NOT NULL CHECK(revision >= 1),
              status TEXT NOT NULL CHECK(status IN ('draft','submitted','school_approved','approved','returned')),
              male_teachers INTEGER NOT NULL DEFAULT 0 CHECK(male_teachers >= 0),
              female_teachers INTEGER NOT NULL DEFAULT 0 CHECK(female_teachers >= 0),
              classrooms INTEGER NOT NULL DEFAULT 0 CHECK(classrooms >= 0),
              notes TEXT NOT NULL DEFAULT '',
              decision_reason TEXT NULL,
              created_by TEXT NOT NULL REFERENCES users(id), created_utc TEXT NOT NULL,
              updated_utc TEXT NOT NULL,
              UNIQUE(school_id, round_id, revision)
            );
            CREATE TABLE IF NOT EXISTS enrollment_rows (
              submission_id TEXT NOT NULL REFERENCES submissions(id) ON DELETE CASCADE,
              grade TEXT NOT NULL, boys INTEGER NOT NULL CHECK(boys >= 0),
              girls INTEGER NOT NULL CHECK(girls >= 0),
              PRIMARY KEY(submission_id, grade)
            );
            CREATE TABLE IF NOT EXISTS decisions (
              id TEXT PRIMARY KEY, submission_id TEXT NOT NULL REFERENCES submissions(id),
              actor_id TEXT NOT NULL REFERENCES users(id), action TEXT NOT NULL,
              reason TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit_events (
              id TEXT PRIMARY KEY, actor_id TEXT NOT NULL REFERENCES users(id),
              action TEXT NOT NULL, entity_id TEXT NOT NULL,
              details TEXT NOT NULL, created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_submissions_school_round ON submissions(school_id, round_id, revision DESC);
            CREATE INDEX IF NOT EXISTS ix_submissions_round_school_revision ON submissions(round_id, school_id, revision DESC);
            CREATE INDEX IF NOT EXISTS ix_submissions_round_status ON submissions(round_id, status);
            CREATE INDEX IF NOT EXISTS ix_audit_date ON audit_events(created_utc DESC);
            """);
        cmd.ExecuteNonQuery();
        MigrateUserPermissions(con);
        MigrateSchoolRecords(con);
    }

    public bool HasUsers()
    {
        using var con = Open();
        using var cmd = Cmd(con, null, "SELECT EXISTS(SELECT 1 FROM users LIMIT 1)");
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public string AcademicYear
    {
        get
        {
            using var con = Open();
            using var cmd = Cmd(con, null, "SELECT value FROM settings WHERE key='academic_year'");
            return cmd.ExecuteScalar()?.ToString() ?? "غير محددة";
        }
    }

    public string DistrictName
    {
        get
        {
            using var con = Open();
            using var cmd = Cmd(con, null, "SELECT value FROM settings WHERE key='district_name'");
            return cmd.ExecuteScalar()?.ToString() ?? "إدارة التربية والتعليم";
        }
    }

    public void InitializeAdministrator(string district, string year, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(district) || district.Length > 120)
            throw new ArgumentException("أدخل اسم المديرية الصحيح.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(year, @"^\d{4}/\d{4}$"))
            throw new ArgumentException("اكتب السنة الدراسية بالصيغة 2026/2027.");
        var parts = year.Split('/');
        if (int.Parse(parts[1], CultureInfo.InvariantCulture) != int.Parse(parts[0], CultureInfo.InvariantCulture) + 1)
            throw new ArgumentException("السنة الدراسية غير متسلسلة.");
        ValidateUsername(username);
        var hash = PasswordService.Hash(password);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var check = Cmd(con, tx, "SELECT count(*) FROM users"))
        {
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("تم إنشاء الحساب الإداري سابقا.");
        }
        var userId = Id();
        using (var cmd = Cmd(con, tx,
            "INSERT INTO users(id,username,password_hash,role,school_id,permission_mask,is_owner,created_utc) VALUES($id,$n,$p,$r,NULL,$perm,1,$at)",
            ("$id", userId), ("$n", username.Trim()), ("$p", hash), ("$r", Roles.Admin),
            ("$perm", (long)PermissionCatalog.All), ("$at", Utc()))) cmd.ExecuteNonQuery();
        using (var cmd = Cmd(con, tx, "INSERT INTO settings(key,value) VALUES('district_name',$d),('academic_year',$y)",
            ("$d", district.Trim()), ("$y", year))) cmd.ExecuteNonQuery();
        using (var cmd = Cmd(con, tx, "INSERT INTO census_rounds(id,academic_year,snapshot_date) VALUES($id,$y,$date)",
            ("$id", Id()), ("$y", year), ("$date", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))) cmd.ExecuteNonQuery();
        Audit(con, tx, userId, "bootstrap", userId, "Initial administrator and district configured");
        tx.Commit();
    }

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 64 ||
            !System.Text.RegularExpressions.Regex.IsMatch(username, @"^[a-zA-Z0-9._-]{3,64}$"))
            throw new ArgumentException("اسم المستخدم يجب أن يتكون من 3 إلى 64 رمزا إنجليزيا أو رقما أو نقطة أو شرطة.");
    }

    public UserAccount? Authenticate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = Cmd(con, tx,
            "SELECT id,username,password_hash,role,school_id,is_active,failed_attempts,lock_until_utc,permission_mask,is_owner,auth_version FROM users WHERE username=$n",
            ("$n", username.Trim()));
        string id, normalized, hash, role;
        string? school, lockDate;
        int active, fails;
        long permissions;
        bool isOwner;
        long authVersion;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            id = Text(r, 0); normalized = Text(r, 1); hash = Text(r, 2); role = Text(r, 3);
            school = NullableText(r, 4); active = r.GetInt32(5); fails = r.GetInt32(6);
            lockDate = NullableText(r, 7);
            permissions = r.GetInt64(8); isOwner = r.GetInt32(9) == 1;
            authVersion = r.GetInt64(10);
        }
        if (active != 1) return null;
        if (DateTimeOffset.TryParse(lockDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var locked)
            && locked > DateTimeOffset.UtcNow) return null;
        if (!PasswordService.Verify(password, hash))
        {
            fails++;
            var lockUntil = fails >= 5 ? DateTimeOffset.UtcNow.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture) : null;
            using var update = Cmd(con, tx,
                "UPDATE users SET failed_attempts=$f,lock_until_utc=$u WHERE id=$id",
                ("$f", lockUntil is null ? fails : 0), ("$u", lockUntil), ("$id", id));
            update.ExecuteNonQuery();
            tx.Commit();
            return null;
        }
        using (var reset = Cmd(con, tx,
            "UPDATE users SET failed_attempts=0, lock_until_utc=NULL WHERE id=$id", ("$id", id))) reset.ExecuteNonQuery();
        tx.Commit();
        return new UserAccount(id, normalized, role, school, true, permissions, isOwner, authVersion);
    }

    public IReadOnlyList<UserAccount> GetUsers(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanCreateUsers(actor)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, "SELECT id,username,role,school_id,is_active,permission_mask,is_owner,auth_version FROM users ORDER BY is_owner DESC,username");
        using var r = cmd.ExecuteReader();
        var list = new List<UserAccount>();
        while (r.Read()) list.Add(ReadAccount(r));
        return list;
    }

    public void CreateUser(UserAccount actor, string username, string password, string role, string? schoolId) =>
        CreateUser(actor, username, password, role, schoolId, (long)PermissionCatalog.DefaultForRole(role));

    public void CreateUser(UserAccount actor, string username, string password, string role,
        string? schoolId, long permissionMask)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanCreateUsers(actor)) throw new UnauthorizedAccessException();
        ValidateUsername(username);
        if (!PermissionCatalog.IsRoleAllowed(role, permissionMask))
            throw new ArgumentException("الصلاحيات المختارة لا تتوافق مع دور المستخدم.");
        if (!actor.IsOwner && (role == Roles.Admin ||
            ((permissionMask & ~actor.PermissionMask) != 0) ||
            ((PermissionFlags)permissionMask).HasFlag(PermissionFlags.ManageUsers)))
            throw new UnauthorizedAccessException("لا يحق لك إنشاء مسؤول جديد أو منح صلاحيات أعلى من صلاحياتك.");
        if (role is Roles.Entry or Roles.Principal)
        {
            if (string.IsNullOrWhiteSpace(schoolId) || !GetSchools(actor).Any(s => s.Id == schoolId && s.IsActive))
                throw new ArgumentException("حدد المدرسة التي يتبع لها المستخدم.");
        }
        else schoolId = null;
        var hash = PasswordService.Hash(password);
        using var con = Open();
        using var tx = con.BeginTransaction();
        var id = Id();
        using (var cmd = Cmd(con, tx,
            "INSERT INTO users(id,username,password_hash,role,school_id,permission_mask,is_owner,created_utc) VALUES($id,$n,$p,$r,$s,$perm,0,$at)",
            ("$id", id), ("$n", username.Trim()), ("$p", hash), ("$r", role),
            ("$s", schoolId), ("$perm", permissionMask), ("$at", Utc()))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "user.create", id,
            $"role={role};scope={schoolId ?? "district"};permissions={permissionMask}");
        tx.Commit();
    }

    private static void Audit(SqliteConnection con, SqliteTransaction tx, string actor, string action, string entity, string details)
    {
        using var cmd = Cmd(con, tx, "INSERT INTO audit_events(id,actor_id,action,entity_id,details,created_utc) VALUES($id,$a,$act,$e,$d,$at)",
            ("$id", Id()), ("$a", actor), ("$act", action), ("$e", entity), ("$d", details), ("$at", Utc()));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<School> GetSchools(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null,
            "SELECT id,code,name,zone,ownership,CASE WHEN stage='أساسي وثانوي' THEN 'مجمع تربوي' ELSE stage END,is_active,gender FROM schools WHERE $scope IS NULL OR id=$scope ORDER BY zone,name",
            ("$scope", actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId ?? "" : null));
        using var r = cmd.ExecuteReader();
        var list = new List<School>();
        while (r.Read()) list.Add(new School(Text(r, 0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), r.GetInt32(6) == 1, Text(r, 7)));
        return list;
    }

    private static void CheckSchoolInput(string code, string name, string zone, string ownership, string stage)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32 ||
            string.IsNullOrWhiteSpace(name) || name.Length > 180 ||
            string.IsNullOrWhiteSpace(zone) || zone.Length > 100 ||
            string.IsNullOrWhiteSpace(stage) || stage.Length > 100 ||
            ownership is not ("حكومية" or "أهلية") ||
            stage is not ("أساسي" or "ثانوي" or "أساسي وثانوي" or "مجمع تربوي"))
            throw new ArgumentException("بيانات المدرسة ناقصة أو غير صحيحة.");
    }

    public void AddSchool(UserAccount actor, string code, string name, string zone, string ownership, string stage, string gender = "مختلطة")
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditSchool(actor)) throw new UnauthorizedAccessException();
        CheckSchoolInput(code, name, zone, ownership, stage);
        CheckGender(gender);
        using var con = Open();
        using var tx = con.BeginTransaction();
        var id = Id();
        using (var cmd = Cmd(con, tx,
            "INSERT INTO schools(id,code,name,zone,ownership,stage,gender,created_utc) VALUES($id,$c,$n,$z,$o,$s,$g,$at)",
            ("$id", id), ("$c", code.Trim()), ("$n", name.Trim()), ("$z", zone.Trim()),
            ("$o", ownership), ("$s", DatabaseStage(stage)), ("$g", gender), ("$at", Utc()))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "school.create", id, $"code={code.Trim()};name={name.Trim()}");
        tx.Commit();
    }

    public int ImportSchools(UserAccount actor, IReadOnlyList<SchoolImport> rows)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditSchool(actor)) throw new UnauthorizedAccessException();
        if (rows.Count == 0 || rows.Count > 3_000) throw new ArgumentException("عدد المدارس في ملف الاستيراد غير صحيح.");
        if (rows.Select(r => r.Code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            throw new ArgumentException("الملف يحتوي رموز مدارس مكررة.");
        foreach (var row in rows) CheckSchoolInput(row.Code, row.Name, row.Zone, row.Ownership, row.Stage);
        using var con = Open();
        using var tx = con.BeginTransaction();

        // Prepare a single statement for the complete atomic batch rather than 1 command per school.
        using var insert = Cmd(con, tx,
            "INSERT INTO schools(id,code,name,zone,ownership,stage,created_utc) VALUES($id,$c,$n,$z,$o,$s,$at)",
            ("$id", ""), ("$c", ""), ("$n", ""), ("$z", ""),
            ("$o", ""), ("$s", ""), ("$at", ""));
        insert.Prepare();
        var createdAt = Utc();
        foreach (var row in rows)
        {
            insert.Parameters["$id"].Value = Id();
            insert.Parameters["$c"].Value = row.Code.Trim();
            insert.Parameters["$n"].Value = row.Name.Trim();
            insert.Parameters["$z"].Value = row.Zone.Trim();
            insert.Parameters["$o"].Value = row.Ownership;
            insert.Parameters["$s"].Value = row.Stage.Trim();
            insert.Parameters["$at"].Value = createdAt;
            insert.ExecuteNonQuery();
        }

        Audit(con, tx, actor.Id, "school.import", "schools", $"count={rows.Count}");
        tx.Commit();
        return rows.Count;
    }

    public void UpdateSchool(UserAccount actor, School school)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditSchool(actor)) throw new UnauthorizedAccessException();
        CheckSchoolInput(school.Code, school.Name, school.Zone, school.Ownership, school.Stage);
        CheckGender(school.Gender);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var cmd = Cmd(con, tx,
            "UPDATE schools SET code=$c,name=$n,zone=$z,ownership=$o,stage=$s,gender=$g,is_active=$a WHERE id=$id",
            ("$id", school.Id), ("$c", school.Code.Trim()), ("$n", school.Name.Trim()),
            ("$z", school.Zone.Trim()), ("$o", school.Ownership), ("$s", DatabaseStage(school.Stage)),
            ("$g", school.Gender), ("$a", school.IsActive ? 1 : 0)))
        {
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("المدرسة غير موجودة.");
        }
        Audit(con, tx, actor.Id, "school.update", school.Id, $"code={school.Code};name={school.Name};active={school.IsActive}");
        tx.Commit();
    }

    private string CurrentRoundId(SqliteConnection con, SqliteTransaction? tx)
    {
        using var cmd = Cmd(con, tx, "SELECT id FROM census_rounds WHERE is_active=1 ORDER BY academic_year DESC LIMIT 1");
        return cmd.ExecuteScalar()?.ToString() ?? throw new InvalidOperationException("لم يتم فتح سنة إحصائية.");
    }

    private CensusSubmission ReadSubmission(SqliteConnection con, string id)
    {
        using var head = Cmd(con, null, """
            SELECT u.id,u.school_id,s.name,r.academic_year,u.revision,u.status,
                   u.male_teachers,u.female_teachers,u.classrooms,u.notes,u.decision_reason,u.created_utc
            FROM submissions u JOIN schools s ON s.id=u.school_id
            JOIN census_rounds r ON r.id=u.round_id WHERE u.id=$id
            """, ("$id", id));
        string schoolId, name, year, status, notes, key;
        string? reason;
        int revision, male, female, rooms;
        DateTimeOffset created;
        using (var r = head.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException("استمارة الإحصاء غير موجودة.");
            key = Text(r, 0); schoolId = Text(r, 1); name = Text(r, 2); year = Text(r, 3);
            revision = r.GetInt32(4); status = Text(r, 5); male = r.GetInt32(6);
            female = r.GetInt32(7); rooms = r.GetInt32(8); notes = Text(r, 9);
            reason = NullableText(r, 10); created = DateTimeOffset.Parse(Text(r, 11), CultureInfo.InvariantCulture);
        }
        using var rows = Cmd(con, null,
            "SELECT grade,boys,girls FROM enrollment_rows WHERE submission_id=$id ORDER BY CAST(grade AS INTEGER)", ("$id", id));
        using var reader = rows.ExecuteReader();
        var lines = new List<EnrollmentLine>();
        while (reader.Read()) lines.Add(new EnrollmentLine(Text(reader, 0), reader.GetInt32(1), reader.GetInt32(2)));
        return new CensusSubmission(key, schoolId, name, year, revision, status,
            new CensusData(lines, male, female, rooms, notes), reason, created);
    }

    public CensusSubmission? GetLatestSubmission(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeSchool(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT id FROM submissions WHERE school_id=$s AND round_id=$r
            ORDER BY revision DESC LIMIT 1
            """, ("$s", schoolId), ("$r", CurrentRoundId(con, null)));
        var id = cmd.ExecuteScalar()?.ToString();
        return id is null ? null : ReadSubmission(con, id);
    }

    public IReadOnlyList<CensusSubmission> GetLatestSubmissions(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            WITH most_recent AS (
                SELECT school_id, MAX(revision) AS revision
                FROM submissions WHERE round_id=$r GROUP BY school_id
            )
            SELECT s.id,s.school_id,school.name,round.academic_year,s.revision,s.status,
                   s.male_teachers,s.female_teachers,s.classrooms,s.notes,s.decision_reason,s.created_utc,
                   e.grade,e.boys,e.girls
            FROM most_recent recent
            JOIN submissions s ON s.school_id=recent.school_id
                 AND s.revision=recent.revision AND s.round_id=$r
            JOIN schools school ON school.id=s.school_id
            JOIN census_rounds round ON round.id=s.round_id
            LEFT JOIN enrollment_rows e ON e.submission_id=s.id
            WHERE ($scope IS NULL OR s.school_id=$scope)
            ORDER BY s.school_id, CAST(e.grade AS INTEGER)
            """, ("$r", CurrentRoundId(con, null)),
                ("$scope", actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId ?? "" : null));
        using var reader = cmd.ExecuteReader();
        var submissions = new List<CensusSubmission>();
        List<EnrollmentLine>? lines = null;
        string? previousId = null;
        while (reader.Read())
        {
            var id = Text(reader, 0);
            if (id != previousId)
            {
                lines = new List<EnrollmentLine>();
                submissions.Add(new CensusSubmission(
                    id, Text(reader, 1), Text(reader, 2), Text(reader, 3),
                    reader.GetInt32(4), Text(reader, 5),
                    new CensusData(lines, reader.GetInt32(6), reader.GetInt32(7),
                        reader.GetInt32(8), Text(reader, 9)),
                    NullableText(reader, 10),
                    DateTimeOffset.Parse(Text(reader, 11), CultureInfo.InvariantCulture)));
                previousId = id;
            }
            if (!reader.IsDBNull(12))
                lines!.Add(new EnrollmentLine(Text(reader, 12), reader.GetInt32(13), reader.GetInt32(14)));
        }
        return submissions;
    }

    public void SaveDraft(UserAccount actor, string schoolId, CensusData data)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditCensus(actor, schoolId)) throw new UnauthorizedAccessException();
        var errors = CensusValidation.Validate(data);
        if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
        var school = GetSchools(actor).FirstOrDefault(x => x.Id == schoolId && x.IsActive)
            ?? throw new InvalidOperationException("لا يمكن إدخال إحصاء لمدرسة غير نشطة.");
        var allowedGrades = school.Stage switch
        {
            "أساسي" => Enumerable.Range(1, 9).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToHashSet(),
            "ثانوي" => Enumerable.Range(10, 3).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToHashSet(),
            _ => Enumerable.Range(1, 12).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToHashSet()
        };
        if (data.Rows.Any(x => !allowedGrades.Contains(x.Grade)))
            throw new ArgumentException("تتضمن الاستمارة صفوفا غير مسموحة لمرحلة المدرسة.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        var roundId = CurrentRoundId(con, tx);
        int revision = 1;
        string? submissionId = null, status = null;
        using (var latest = Cmd(con, tx,
            "SELECT id,revision,status FROM submissions WHERE school_id=$s AND round_id=$r ORDER BY revision DESC LIMIT 1",
            ("$s", schoolId), ("$r", roundId)))
        using (var r = latest.ExecuteReader())
        {
            if (r.Read()) { submissionId = Text(r, 0); revision = r.GetInt32(1); status = Text(r, 2); }
        }
        if (status is not (null or Statuses.Draft or Statuses.Returned))
            throw new InvalidOperationException("هذه الاستمارة مغلقة. يجب إرجاعها للمراجعة أولا.");
        var now = Utc();
        if (status != Statuses.Draft)
        {
            revision = status is null ? 1 : revision + 1;
            submissionId = Id();
            using var ins = Cmd(con, tx, """
                INSERT INTO submissions(id,school_id,round_id,revision,status,male_teachers,female_teachers,classrooms,notes,created_by,created_utc,updated_utc)
                VALUES($id,$s,$r,$v,'draft',$m,$f,$c,$n,$a,$at,$at)
                """, ("$id", submissionId), ("$s", schoolId), ("$r", roundId), ("$v", revision),
                    ("$m", data.MaleTeachers), ("$f", data.FemaleTeachers), ("$c", data.Classrooms),
                    ("$n", data.Notes), ("$a", actor.Id), ("$at", now));
            ins.ExecuteNonQuery();
        }
        else
        {
            using var upd = Cmd(con, tx, """
                UPDATE submissions SET male_teachers=$m,female_teachers=$f,classrooms=$c,notes=$n,updated_utc=$at WHERE id=$id AND status='draft'
                """, ("$m", data.MaleTeachers), ("$f", data.FemaleTeachers), ("$c", data.Classrooms),
                ("$n", data.Notes), ("$at", now), ("$id", submissionId));
            if (upd.ExecuteNonQuery() != 1) throw new InvalidOperationException("تغيرت حالة الاستمارة أثناء التعديل.");
            using var del = Cmd(con, tx, "DELETE FROM enrollment_rows WHERE submission_id=$id", ("$id", submissionId));
            del.ExecuteNonQuery();
        }
        foreach (var row in data.Rows)
        {
            using var ins = Cmd(con, tx,
                "INSERT INTO enrollment_rows(submission_id,grade,boys,girls) VALUES($id,$g,$b,$f)",
                ("$id", submissionId), ("$g", row.Grade), ("$b", row.Boys), ("$f", row.Girls));
            ins.ExecuteNonQuery();
        }
        Audit(con, tx, actor.Id, "census.save", submissionId!, $"revision={revision};students={data.Students}");
        tx.Commit();
    }

    public void ChangeStatus(UserAccount actor, string submissionId, string action, string reason = "")
    {
        RequireFreshActor(actor);
        using var con = Open();
        using var tx = con.BeginTransaction();
        string schoolId, status, roundId;
        int revision;
        using (var cmd = Cmd(con, tx, "SELECT school_id,status,round_id,revision FROM submissions WHERE id=$id", ("$id", submissionId)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException("الاستمارة غير موجودة.");
            schoolId = Text(r, 0); status = Text(r, 1); roundId = Text(r, 2); revision = r.GetInt32(3);
        }
        if (!AccessPolicy.CanPerform(actor, action, schoolId, status))
            throw new UnauthorizedAccessException("غير مخول بهذا الإجراء أو حالة الاستمارة لا تسمح به.");
        if (CurrentRoundId(con, tx) != roundId) throw new InvalidOperationException("الاستمارة تتبع دورة غير نشطة.");
        using (var latest = Cmd(con, tx,
            "SELECT MAX(revision) FROM submissions WHERE school_id=$s AND round_id=$r",
            ("$s", schoolId), ("$r", roundId)))
        {
            if (Convert.ToInt32(latest.ExecuteScalar(), CultureInfo.InvariantCulture) != revision)
                throw new InvalidOperationException("توجد مراجعة أحدث لهذه الاستمارة.");
        }
        if (action == "return" && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("يجب توضيح سبب إعادة الإحصاء.");
        var target = action switch
        {
            "submit" => Statuses.Submitted,
            "school_approve" => Statuses.SchoolApproved,
            "district_approve" => Statuses.Approved,
            "return" => Statuses.Returned,
            _ => throw new ArgumentException("عملية غير معروفة")
        };
        using (var update = Cmd(con, tx,
            "UPDATE submissions SET status=$to,decision_reason=$reason,updated_utc=$at WHERE id=$id AND status=$from",
            ("$to", target), ("$reason", action == "return" ? reason.Trim() : null),
            ("$at", Utc()), ("$id", submissionId), ("$from", status)))
        {
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("تغيرت حالة الاستمارة؛ أعد المحاولة.");
        }
        using (var dec = Cmd(con, tx,
            "INSERT INTO decisions(id,submission_id,actor_id,action,reason,created_utc) VALUES($id,$s,$a,$action,$reason,$at)",
            ("$id", Id()), ("$s", submissionId), ("$a", actor.Id), ("$action", action),
            ("$reason", reason.Trim()), ("$at", Utc()))) dec.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "census." + action, submissionId, $"from={status};to={target};reason={reason.Trim()}");
        tx.Commit();
    }

    public DashboardStats GetDashboard(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            WITH most_recent AS (
                SELECT school_id, MAX(revision) AS revision
                FROM submissions WHERE round_id=$r GROUP BY school_id
            ),
            latest AS (
                SELECT s.id,s.school_id,s.status,s.male_teachers,s.female_teachers,s.classrooms
                FROM most_recent recent
                JOIN submissions s ON s.school_id=recent.school_id
                     AND s.revision=recent.revision AND s.round_id=$r
                WHERE ($scope IS NULL OR s.school_id=$scope)
            ),
            approved_enrollment AS (
                SELECT e.submission_id, SUM(e.boys + e.girls) AS students
                FROM enrollment_rows e
                JOIN latest l ON l.id=e.submission_id
                WHERE l.status='approved'
                GROUP BY e.submission_id
            )
            SELECT (SELECT COUNT(*) FROM schools WHERE is_active=1
                    AND ($scope IS NULL OR id=$scope)),
                   COALESCE(SUM(CASE WHEN l.status IN ('submitted','school_approved') THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN l.status='approved' THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN l.status='approved' THEN COALESCE(e.students,0) ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN l.status='approved' THEN l.male_teachers+l.female_teachers ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN l.status='approved' THEN l.classrooms ELSE 0 END),0)
            FROM latest l LEFT JOIN approved_enrollment e ON e.submission_id=l.id
            """, ("$r", CurrentRoundId(con, null)),
                ("$scope", actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId ?? "" : null));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("تعذر حساب المؤشرات.");
        return new DashboardStats(
            checked((int)reader.GetInt64(0)), checked((int)reader.GetInt64(1)),
            checked((int)reader.GetInt64(2)), checked((int)reader.GetInt64(3)),
            checked((int)reader.GetInt64(4)), checked((int)reader.GetInt64(5)));
    }

    public IReadOnlyList<AuditItem> GetRecentAudit(UserAccount actor, int limit = 100)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewAudit))
            throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT a.created_utc,u.username,a.action,a.entity_id,a.details
            FROM audit_events a JOIN users u ON u.id=a.actor_id
            ORDER BY a.created_utc DESC LIMIT $l
            """, ("$l", Math.Clamp(limit, 1, 500)));
        var list = new List<AuditItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new AuditItem(DateTimeOffset.Parse(Text(r, 0), CultureInfo.InvariantCulture),
            Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4)));
        return list;
    }
}
