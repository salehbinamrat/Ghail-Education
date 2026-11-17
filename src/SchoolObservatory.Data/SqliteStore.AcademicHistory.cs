using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>Phase 2: additive, transaction-safe academic-year and stable identity history.</summary>
public sealed partial class SqliteStore
{
    public int SchemaVersion
    {
        get
        {
            using var con = Open();
            using var cmd = Cmd(con, null, "PRAGMA user_version;");
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private static void MigrateAcademicHistory(SqliteConnection con)
    {
        using var schema = Cmd(con, null, """
            CREATE TABLE IF NOT EXISTS academic_years (
              year TEXT PRIMARY KEY,
              status TEXT NOT NULL DEFAULT 'open' CHECK(status IN ('open','closed')),
              is_active INTEGER NOT NULL DEFAULT 0 CHECK(is_active IN (0,1)),
              opened_utc TEXT NOT NULL,
              closed_utc TEXT NULL,
              CHECK(status='open' OR is_active=0)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_one_active_academic_year
              ON academic_years(is_active) WHERE is_active=1;
            CREATE TABLE IF NOT EXISTS student_people (
              id TEXT PRIMARY KEY, created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS student_person_links (
              student_id TEXT PRIMARY KEY REFERENCES school_students(id) ON DELETE RESTRICT,
              person_id TEXT NOT NULL REFERENCES student_people(id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_student_person_links_person ON student_person_links(person_id);
            CREATE TABLE IF NOT EXISTS student_placement_events (
              id TEXT PRIMARY KEY,
              person_id TEXT NOT NULL REFERENCES student_people(id) ON DELETE RESTRICT,
              student_id TEXT NOT NULL REFERENCES school_students(id) ON DELETE RESTRICT,
              school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
              academic_year TEXT NOT NULL REFERENCES academic_years(year) ON DELETE RESTRICT,
              grade INTEGER NOT NULL CHECK(grade BETWEEN 1 AND 12),
              section TEXT NOT NULL,
              action TEXT NOT NULL CHECK(action IN
                ('legacy','enroll','promote','repeat','section_transfer','school_transfer','graduate','withdraw')),
              created_utc TEXT NOT NULL,
              actor_id TEXT NULL REFERENCES users(id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_placement_person_year
              ON student_placement_events(person_id,academic_year,created_utc);
            CREATE TABLE IF NOT EXISTS student_year_decisions (
              person_id TEXT NOT NULL REFERENCES student_people(id) ON DELETE RESTRICT,
              academic_year TEXT NOT NULL REFERENCES academic_years(year) ON DELETE RESTRICT,
              action TEXT NOT NULL CHECK(action IN ('promote','repeat','graduate','withdraw')),
              event_id TEXT NOT NULL UNIQUE REFERENCES student_placement_events(id) ON DELETE RESTRICT,
              PRIMARY KEY(person_id,academic_year)
            );
            CREATE TABLE IF NOT EXISTS school_rooms (
              id TEXT PRIMARY KEY, school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
              room_code TEXT NOT NULL, building TEXT NOT NULL DEFAULT '',
              capacity INTEGER NULL CHECK(capacity IS NULL OR capacity>=0),
              UNIQUE(school_id,room_code), UNIQUE(id,school_id)
            );
            CREATE TABLE IF NOT EXISTS section_room_assignments (
              grade_id TEXT PRIMARY KEY REFERENCES school_grades(id) ON DELETE RESTRICT,
              school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
              room_id TEXT NULL,
              FOREIGN KEY(room_id,school_id) REFERENCES school_rooms(id,school_id) ON DELETE RESTRICT
            );
            CREATE TABLE IF NOT EXISTS school_infrastructure (
              school_id TEXT PRIMARY KEY REFERENCES schools(id) ON DELETE RESTRICT,
              district TEXT NOT NULL DEFAULT '', subdistrict TEXT NOT NULL DEFAULT '',
              village TEXT NOT NULL DEFAULT '', address_details TEXT NOT NULL DEFAULT '',
              latitude REAL NULL CHECK(latitude BETWEEN -90 AND 90),
              longitude REAL NULL CHECK(longitude BETWEEN -180 AND 180),
              building_type TEXT NOT NULL DEFAULT '', building_condition TEXT NOT NULL DEFAULT '',
              classrooms_count INTEGER NULL CHECK(classrooms_count IS NULL OR classrooms_count>=0),
              operating_shifts TEXT NOT NULL DEFAULT '', updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS school_facilities (
              school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
              facility_type TEXT NOT NULL, quantity INTEGER NOT NULL CHECK(quantity>=0),
              notes TEXT NOT NULL DEFAULT '', PRIMARY KEY(school_id,facility_type)
            );
            CREATE TABLE IF NOT EXISTS employee_people (
              id TEXT PRIMARY KEY, employee_code TEXT NOT NULL,
              full_name TEXT NOT NULL, created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS employee_staff_links (
              staff_id TEXT PRIMARY KEY REFERENCES school_staff(id) ON DELETE RESTRICT,
              person_id TEXT NOT NULL REFERENCES employee_people(id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_employee_person_links ON employee_staff_links(person_id);
            CREATE TABLE IF NOT EXISTS employee_assignments (
              id TEXT PRIMARY KEY,
              person_id TEXT NOT NULL REFERENCES employee_people(id) ON DELETE RESTRICT,
              payroll_school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
              workplace_school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
              assignment_type TEXT NOT NULL CHECK(assignment_type IN
                ('original','assignment','secondment','transfer')),
              start_date TEXT NOT NULL, end_date TEXT NULL,
              CHECK(end_date IS NULL OR end_date>=start_date),
              notes TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL,
              actor_id TEXT NULL REFERENCES users(id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_employee_assignments_person
              ON employee_assignments(person_id,start_date);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_employee_active_assignment
              ON employee_assignments(person_id) WHERE end_date IS NULL;
            """);
        schema.ExecuteNonQuery();

        // Never guess a legacy attendance year from a calendar date. Legacy rows
        // remain null/immutable; all new attendance rows explicitly bind to a year.
        using (var column = Cmd(con, null,
            "SELECT COUNT(*) FROM pragma_table_info('student_attendance') WHERE name='academic_year'"))
            if (Convert.ToInt32(column.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using var alter = Cmd(con, null,
                    "ALTER TABLE student_attendance ADD COLUMN academic_year TEXT NULL REFERENCES academic_years(year)");
                alter.ExecuteNonQuery();
            }
        // Existing Phase 1 years, students and staff are mapped without changing their IDs.
        using var seed = Cmd(con, null, """
            INSERT OR IGNORE INTO academic_years(year,status,is_active,opened_utc)
            SELECT academic_year,'open',0,$now FROM census_rounds;
            INSERT OR IGNORE INTO academic_years(year,status,is_active,opened_utc)
            SELECT academic_year,'open',0,$now FROM school_grades;
            INSERT OR IGNORE INTO academic_years(year,status,is_active,opened_utc)
            SELECT academic_year,'open',0,$now FROM student_enrollments;
            INSERT OR IGNORE INTO academic_years(year,status,is_active,opened_utc)
            SELECT value,'open',0,$now FROM settings WHERE key='academic_year';
            INSERT OR IGNORE INTO student_people(id,created_utc)
            SELECT s.id,s.created_utc FROM school_students s
            WHERE NOT EXISTS(SELECT 1 FROM student_person_links l WHERE l.student_id=s.id);
            INSERT OR IGNORE INTO student_person_links(student_id,person_id)
            SELECT id,id FROM school_students;
            INSERT OR IGNORE INTO employee_people(id,employee_code,full_name,created_utc)
            SELECT s.id,s.employee_code,s.full_name,s.created_utc FROM school_staff s
            WHERE NOT EXISTS(SELECT 1 FROM employee_staff_links l WHERE l.staff_id=s.id);
            INSERT OR IGNORE INTO employee_staff_links(staff_id,person_id)
            SELECT id,id FROM school_staff;
            """, ("$now", Utc()));
        seed.ExecuteNonQuery();

        using (var active = Cmd(con, null, """
            UPDATE academic_years SET is_active=1
            WHERE year=(SELECT value FROM settings WHERE key='academic_year')
              AND status='open'
              AND NOT EXISTS(SELECT 1 FROM academic_years WHERE is_active=1)
            """)) active.ExecuteNonQuery();

        // Backfill the legacy enrollment snapshot once; repeat migrations are idempotent.
        using var history = Cmd(con, null, """
            INSERT INTO student_placement_events
              (id,person_id,student_id,school_id,academic_year,grade,section,action,created_utc)
            SELECT lower(hex(randomblob(16))),link.person_id,en.student_id,en.school_id,
                   en.academic_year,g.grade,g.section,'legacy',en.updated_utc
            FROM student_enrollments en
            JOIN school_grades g ON g.id=en.grade_id
            JOIN student_person_links link ON link.student_id=en.student_id
            WHERE NOT EXISTS(
              SELECT 1 FROM student_placement_events e
              WHERE e.student_id=en.student_id AND e.academic_year=en.academic_year
                )
            """);
        history.ExecuteNonQuery();

        // Database-level enforcement also covers older UI and raw legacy write paths.
        foreach (var (table, yearExpression) in new (string Table, string Year)[]
        {
            ("school_grades", "academic_year"),
            ("student_enrollments", "academic_year"),
            ("student_results", "academic_year"),
            ("student_assessments", "academic_year"),
            ("student_attendance", "academic_year"),
            ("submissions", "round_id"),
            ("enrollment_rows", "submission_id"),
            ("decisions", "submission_id"),
            ("census_rounds", "academic_year"),
            ("student_placement_events", "academic_year")
        })
        {
            // Academic-year column is explicit for new attendance. Legacy null rows
            // must remain immutable until an authorized historical reconciliation.
            foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var prefix = action == "DELETE" ? "OLD" : "NEW";
                string YearExpr(string row) => table switch
                {
                    "submissions" => $"(SELECT academic_year FROM census_rounds WHERE id={row}.round_id)",
                    "enrollment_rows" or "decisions" =>
                        $"(SELECT r.academic_year FROM submissions s JOIN census_rounds r ON r.id=s.round_id WHERE s.id={row}.submission_id)",
                    _ => $"{row}.academic_year"
                };
                var oldCheck = action == "UPDATE" ? $"""
                    OR EXISTS(SELECT 1 FROM academic_years
                      WHERE year={YearExpr("OLD")} AND status='closed')
                    """ : "";
                var unknownAttendance = table == "student_attendance"
                    ? action switch
                    {
                        "INSERT" => " OR NEW.academic_year IS NULL",
                        "UPDATE" => " OR OLD.academic_year IS NULL OR NEW.academic_year IS NULL",
                        _ => " OR OLD.academic_year IS NULL"
                    }
                    : "";
                var expr = YearExpr(prefix);
                using var trigger = Cmd(con, null, $"""
                    CREATE TRIGGER IF NOT EXISTS phase2_lock_{table}_{action.ToLowerInvariant()}
                    BEFORE {action} ON {table}
                    WHEN EXISTS(SELECT 1 FROM academic_years
                      WHERE year={expr} AND status='closed') {oldCheck} {unknownAttendance}
                    BEGIN SELECT RAISE(ABORT,'Academic year is closed'); END;
                    """);
                trigger.ExecuteNonQuery();
            }
        }
        using var noReopen = Cmd(con, null, """
            CREATE TRIGGER IF NOT EXISTS phase2_no_reopen
            BEFORE UPDATE OF status ON academic_years
            WHEN OLD.status='closed' AND NEW.status<>'closed'
            BEGIN SELECT RAISE(ABORT,'Closed academic years cannot be reopened'); END;
            """);
        noReopen.ExecuteNonQuery();
    }

    private static void ValidateAcademicYear(string year)
    {
        if (!Regex.IsMatch(year ?? "", @"^\d{4}/\d{4}$") ||
            !int.TryParse(year[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var first) ||
            first < 1900 || first >= 9999 ||
            year[5..] != (first + 1).ToString("0000", CultureInfo.InvariantCulture))
            throw new ArgumentException("Academic year must use YYYY/YYYY with consecutive years.", nameof(year));
    }

    private void RequireYearAdministrator(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!actor.IsOwner && !(actor.Role is Roles.Admin or Roles.Statistician &&
            AccessPolicy.Has(actor, PermissionFlags.ManageSchools)))
            throw new UnauthorizedAccessException("Academic-year administration is restricted.");
    }

    public IReadOnlyList<AcademicYearRecord> GetAcademicYears(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, "SELECT year,status,is_active FROM academic_years ORDER BY year DESC");
        using var reader = cmd.ExecuteReader();
        var rows = new List<AcademicYearRecord>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) == 1));
        return rows;
    }

    public void OpenAcademicYear(UserAccount actor, string year)
    {
        RequireYearAdministrator(actor);
        ValidateAcademicYear(year);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var check = Cmd(con, tx, "SELECT COUNT(*) FROM academic_years WHERE year=$y",
            ("$y", year)))
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Academic year already exists or was closed.");
        using (var insert = Cmd(con, tx,
            "INSERT INTO academic_years(year,status,is_active,opened_utc) VALUES($y,'open',0,$at)",
            ("$y", year), ("$at", Utc()))) insert.ExecuteNonQuery();
        using (var round = Cmd(con, tx, """
            INSERT OR IGNORE INTO census_rounds(id,academic_year,snapshot_date,is_active)
            VALUES($id,$y,$date,0)
            """, ("$id", Id()), ("$y", year), ("$date", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))
            round.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "academic_year.open", year, "");
        tx.Commit();
    }

    public void SetActiveAcademicYear(UserAccount actor, string year)
    {
        RequireYearAdministrator(actor);
        ValidateAcademicYear(year);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var check = Cmd(con, tx,
            "SELECT status FROM academic_years WHERE year=$y", ("$y", year)))
            if (check.ExecuteScalar()?.ToString() != "open")
                throw new InvalidOperationException("Cannot activate an unknown or closed year.");
        using (var clear = Cmd(con, tx, "UPDATE academic_years SET is_active=0 WHERE is_active=1; UPDATE census_rounds SET is_active=0 WHERE is_active=1;"))
            clear.ExecuteNonQuery();
        using (var activate = Cmd(con, tx,
            "UPDATE academic_years SET is_active=1 WHERE year=$y; UPDATE census_rounds SET is_active=1 WHERE academic_year=$y;",
            ("$y", year))) activate.ExecuteNonQuery();
        using (var setting = Cmd(con, tx, """
            INSERT INTO settings(key,value) VALUES('academic_year',$y)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """, ("$y", year))) setting.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "academic_year.activate", year, "");
        tx.Commit();
    }

    public void CloseAcademicYear(UserAccount actor, string year)
    {
        RequireYearAdministrator(actor);
        ValidateAcademicYear(year);
        using var con = Open();
        using var tx = con.BeginTransaction();
        // Materialize current student identity and enrollment history BEFORE closing.
        // This allows later promotion without inserting into locked historical years.
        using (var people = Cmd(con, tx, """
            INSERT OR IGNORE INTO student_people(id,created_utc)
            SELECT id,created_utc FROM school_students;
            INSERT OR IGNORE INTO student_person_links(student_id,person_id)
            SELECT id,id FROM school_students;
            """)) people.ExecuteNonQuery();
        using (var history = Cmd(con, tx, """
            INSERT INTO student_placement_events
              (id,person_id,student_id,school_id,academic_year,grade,section,action,created_utc)
            SELECT lower(hex(randomblob(16))),l.person_id,en.student_id,en.school_id,
                   en.academic_year,g.grade,g.section,'legacy',en.updated_utc
            FROM student_enrollments en JOIN school_grades g ON g.id=en.grade_id
            JOIN student_person_links l ON l.student_id=en.student_id
            WHERE en.academic_year=$y AND NOT EXISTS(
              SELECT 1 FROM student_placement_events e
              WHERE e.student_id=en.student_id AND e.academic_year=en.academic_year)
            """, ("$y", year))) history.ExecuteNonQuery();
        using (var round = Cmd(con, tx, "UPDATE census_rounds SET is_active=0 WHERE academic_year=$y AND is_active=1",
            ("$y", year))) round.ExecuteNonQuery();
        using (var cmd = Cmd(con, tx, """
            UPDATE academic_years SET status='closed',is_active=0,closed_utc=$at
            WHERE year=$y AND status='open'
            """, ("$y", year), ("$at", Utc())))
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Year already closed or unknown.");
        Audit(con, tx, actor.Id, "academic_year.close", year, "");
        tx.Commit();
    }

    public IReadOnlyList<StudentPlacementHistory> GetStudentHistory(UserAccount actor, string schoolId, string studentId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT e.academic_year,e.school_id,e.grade,e.section,e.action,e.student_id
            FROM student_placement_events e
            WHERE e.person_id=(SELECT person_id FROM student_person_links WHERE student_id=$id)
              AND EXISTS(SELECT 1 FROM school_students s WHERE s.id=$id AND s.school_id=$school)
              AND ($district=1 OR e.school_id=$school)
            ORDER BY e.academic_year,e.created_utc,e.id
            """, ("$id", studentId), ("$school", schoolId),
            ("$district", actor.Role is Roles.Entry or Roles.Principal ? 0 : 1));
        using var reader = cmd.ExecuteReader();
        var rows = new List<StudentPlacementHistory>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1),
            reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return rows;
    }

    /// <summary>Transactional year promotion/repeat or same-year section transfer.
    /// Cross-school moves require district-level authority and retain the original student row.
    /// </summary>
    public void TransitionStudent(UserAccount actor, string schoolId, string studentId,
        string targetSchoolId, int targetGrade, string targetSection, string action)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudents(actor, schoolId) ||
            !AccessPolicy.CanManageStudents(actor, targetSchoolId))
            throw new UnauthorizedAccessException();
        if (schoolId != targetSchoolId && actor.Role is Roles.Entry or Roles.Principal)
            throw new UnauthorizedAccessException("Cross-school transfers require district authority.");
        if (action is not ("promote" or "repeat" or "section_transfer" or "school_transfer"))
            throw new ArgumentException("Invalid transition action.", nameof(action));
        if (action == "school_transfer" != (schoolId != targetSchoolId))
            throw new ArgumentException("School transfer action must match the destination school.");
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var yearCheck = Cmd(con, tx, """
            SELECT status FROM academic_years WHERE year=$y AND is_active=1
            """, ("$y", year)))
            if (yearCheck.ExecuteScalar()?.ToString() != "open")
                throw new InvalidOperationException("Target year is not open and active.");
        string personId;
        string code;
        string name;
        string gender;
        string guardian;
        string phone;
        string notes;
        using (var student = Cmd(con, tx, """
            SELECT s.student_code,s.full_name,s.gender,s.guardian_name,s.guardian_phone,s.notes,
                   COALESCE(l.person_id,s.id)
            FROM school_students s LEFT JOIN student_person_links l ON l.student_id=s.id
            WHERE s.id=$id AND s.school_id=$school
            """, ("$id", studentId), ("$school", schoolId)))
        using (var reader = student.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Source student not found.");
            code=reader.GetString(0); name=reader.GetString(1); gender=reader.GetString(2);
            guardian=reader.GetString(3); phone=reader.GetString(4); notes=reader.GetString(5);
            personId=reader.GetString(6);
        }
        using (var ensure = Cmd(con, tx,
            "INSERT OR IGNORE INTO student_people(id,created_utc) VALUES($id,$at); INSERT OR IGNORE INTO student_person_links(student_id,person_id) VALUES($s,$id)",
            ("$id", personId), ("$s", studentId), ("$at", Utc()))) ensure.ExecuteNonQuery();
        var current = new List<(string Year,int Grade,string Section)>();
        using (var prior = Cmd(con, tx, """
            SELECT en.academic_year,g.grade,g.section FROM student_enrollments en
            JOIN school_grades g ON g.id=en.grade_id WHERE en.student_id=$id
            ORDER BY en.academic_year DESC
            """, ("$id", studentId)))
        using (var reader = prior.ExecuteReader())
            while (reader.Read()) current.Add((reader.GetString(0),reader.GetInt32(1),reader.GetString(2)));
        if (current.Count == 0) throw new InvalidOperationException("Student has no enrollment.");
        var latest=current[0];
        using (var status = Cmd(con, tx, """
            SELECT status FROM student_enrollments
            WHERE student_id=$id AND academic_year=$y
            """, ("$id", studentId), ("$y", latest.Year)))
            if (status.ExecuteScalar()?.ToString() != "مسجل")
                throw new InvalidOperationException("Student is no longer actively enrolled at the source school.");
        using (var already = Cmd(con, tx, """
            SELECT COUNT(*) FROM student_enrollments en
            JOIN student_person_links link ON link.student_id=en.student_id
            WHERE link.person_id=$person AND en.academic_year=$year
              AND en.student_id<>$source AND en.status='مسجل'
            """, ("$person", personId), ("$year", year), ("$source", studentId)))
            if (Convert.ToInt32(already.ExecuteScalar(), CultureInfo.InvariantCulture)>0)
                throw new InvalidOperationException("Student already has an active enrollment in another school.");

        if (action == "promote" && (latest.Year == year || targetGrade != latest.Grade + 1))
            throw new InvalidOperationException("Promotion must advance one grade to a new year.");
        if (action == "repeat" && (latest.Year == year || targetGrade != latest.Grade))
            throw new InvalidOperationException("Repeating requires a new year and the same grade.");
        if (action == "section_transfer" && (latest.Year != year || targetGrade != latest.Grade))
            throw new InvalidOperationException("Section transfer must remain within the current grade and year.");
        if (action == "school_transfer" && latest.Year != year)
            throw new InvalidOperationException("School transfer requires a current-year enrollment.");
        if (action == "promote" || action == "repeat")
        {
            using var duplicate = Cmd(con, tx, "SELECT COUNT(*) FROM student_year_decisions WHERE person_id=$p AND academic_year=$y",
                ("$p", personId), ("$y", year));
            if (Convert.ToInt32(duplicate.ExecuteScalar(), CultureInfo.InvariantCulture)>0)
                throw new InvalidOperationException("Student already has a year transition.");
        }
        var destination = StudentSchool(con, tx, targetSchoolId);
        GradeAllowed(destination, targetGrade);
        _ = StudentGender(destination, gender);
        var targetId = GradeId(con, tx, targetSchoolId, year, targetGrade, SectionName(targetSection));
        var destStudentId = studentId;
        if (schoolId != targetSchoolId)
        {
            destStudentId=Id();
            using var create = Cmd(con, tx, """
                INSERT INTO school_students(id,school_id,student_code,full_name,gender,
                    guardian_name,guardian_phone,notes,is_active,created_utc,updated_utc)
                VALUES($id,$school,$code,$name,$gender,$guardian,$phone,$notes,1,$at,$at)
                """, ("$id", destStudentId), ("$school", targetSchoolId), ("$code", code),
                ("$name", name), ("$gender", gender), ("$guardian", guardian),
                ("$phone", phone), ("$notes", notes), ("$at", Utc()));
            create.ExecuteNonQuery();
            using var link = Cmd(con, tx,
                "INSERT INTO student_person_links(student_id,person_id) VALUES($id,$p)",
                ("$id", destStudentId), ("$p", personId));
            link.ExecuteNonQuery();
        }
        using (var existing = Cmd(con, tx, """
            SELECT COUNT(*) FROM student_enrollments WHERE student_id=$id AND academic_year=$y
            """, ("$id", destStudentId), ("$y", year)))
            if (Convert.ToInt32(existing.ExecuteScalar(), CultureInfo.InvariantCulture)>0 &&
                action is not ("section_transfer" or "school_transfer"))
                throw new InvalidOperationException("Student is already enrolled in the target year.");
        using (var enroll = Cmd(con, tx, """
            INSERT INTO student_enrollments(student_id,school_id,academic_year,grade_id,status,updated_utc)
            VALUES($id,$school,$y,$grade,'مسجل',$at)
            ON CONFLICT(student_id,academic_year)
            DO UPDATE SET grade_id=excluded.grade_id,updated_utc=excluded.updated_utc
            """, ("$id", destStudentId), ("$school", targetSchoolId),
            ("$y", year), ("$grade", targetId), ("$at", Utc()))) enroll.ExecuteNonQuery();
        if (schoolId != targetSchoolId)
        {
            using var archive = Cmd(con, tx,
                "UPDATE student_enrollments SET status='منقول',updated_utc=$at WHERE student_id=$id AND academic_year=$y",
                ("$id", studentId), ("$y", year), ("$at", Utc()));
            archive.ExecuteNonQuery();
        }
        // Existing enrollments created after the v2 migration still need a historical
        // source event before the next-year decision. Never rewrite the old enrollment.
        using (var sourceHistory = Cmd(con, tx, """
            INSERT INTO student_placement_events
              (id,person_id,student_id,school_id,academic_year,grade,section,action,created_utc,actor_id)
            SELECT $id,$p,$student,$school,$year,$grade,$section,'legacy',$at,$actor
            WHERE NOT EXISTS(
              SELECT 1 FROM student_placement_events
              WHERE student_id=$student AND academic_year=$year)
            """, ("$id", Id()), ("$p", personId), ("$student", studentId),
            ("$school", schoolId), ("$year", latest.Year), ("$grade", latest.Grade),
            ("$section", latest.Section), ("$at", Utc()), ("$actor", actor.Id)))
            sourceHistory.ExecuteNonQuery();
        var eventId=Id();
        using (var history = Cmd(con, tx, """
            INSERT INTO student_placement_events
              (id,person_id,student_id,school_id,academic_year,grade,section,action,created_utc,actor_id)
            VALUES($id,$p,$student,$school,$y,$grade,$section,$action,$at,$actor)
            """, ("$id", eventId), ("$p", personId), ("$student", destStudentId),
            ("$school", targetSchoolId), ("$y", year), ("$grade", targetGrade),
            ("$section", SectionName(targetSection)), ("$action", action),
            ("$at", Utc()), ("$actor", actor.Id))) history.ExecuteNonQuery();
        if (action is "promote" or "repeat")
        {
            using var decision = Cmd(con, tx, """
                INSERT INTO student_year_decisions(person_id,academic_year,action,event_id)
                VALUES($p,$y,$action,$event)
                """, ("$p", personId), ("$y", year), ("$action", action), ("$event", eventId));
            decision.ExecuteNonQuery();
        }
        Audit(con, tx, actor.Id, "student."+action, personId, $"from={schoolId};to={targetSchoolId};year={year}");
        tx.Commit();
    }

    public void CompleteStudentEnrollment(UserAccount actor, string schoolId, string studentId, string action)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudents(actor, schoolId)) throw new UnauthorizedAccessException();
        if (action is not ("graduate" or "withdraw"))
            throw new ArgumentException("Completion must be graduate or withdraw.", nameof(action));
        var year=AcademicYear;
        using var con=Open();
        using var tx=con.BeginTransaction();
        using (var open=Cmd(con,tx,
            "SELECT status FROM academic_years WHERE year=$year AND is_active=1",("$year",year)))
            if (open.ExecuteScalar()?.ToString()!="open")
                throw new InvalidOperationException("Cannot change a closed or inactive academic year.");
        string personId;
        int grade;
        string section;
        using (var student=Cmd(con,tx, """
            SELECT COALESCE(l.person_id,s.id),g.grade,g.section
            FROM school_students s
            JOIN student_enrollments e ON e.student_id=s.id AND e.school_id=s.school_id
            JOIN school_grades g ON g.id=e.grade_id
            LEFT JOIN student_person_links l ON l.student_id=s.id
            WHERE s.id=$id AND s.school_id=$school AND e.academic_year=$year
              AND e.status='مسجل' AND s.is_active=1
            """,("$id",studentId),("$school",schoolId),("$year",year)))
        using (var reader=student.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Student is not actively enrolled.");
            personId=reader.GetString(0);grade=reader.GetInt32(1);section=reader.GetString(2);
        }
        using (var ensure=Cmd(con,tx, """
            INSERT OR IGNORE INTO student_people(id,created_utc) VALUES($p,$at);
            INSERT OR IGNORE INTO student_person_links(student_id,person_id) VALUES($student,$p);
            """,("$p",personId),("$student",studentId),("$at",Utc())))
            ensure.ExecuteNonQuery();
        using (var duplicate=Cmd(con,tx,
            "SELECT COUNT(*) FROM student_year_decisions WHERE person_id=$p AND academic_year=$year",
            ("$p",personId),("$year",year)))
            if (Convert.ToInt32(duplicate.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
                throw new InvalidOperationException("Student has already completed a year decision.");
        var eventId=Id();
        using (var ev=Cmd(con,tx, """
            INSERT INTO student_placement_events
              (id,person_id,student_id,school_id,academic_year,grade,section,action,created_utc,actor_id)
            VALUES($id,$p,$student,$school,$year,$grade,$section,$action,$at,$actor)
            """,("$id",eventId),("$p",personId),("$student",studentId),("$school",schoolId),
            ("$year",year),("$grade",grade),("$section",section),("$action",action),
            ("$at",Utc()),("$actor",actor.Id))) ev.ExecuteNonQuery();
        using (var decision=Cmd(con,tx, """
            INSERT INTO student_year_decisions(person_id,academic_year,action,event_id)
            VALUES($p,$year,$action,$event)
            """,("$p",personId),("$year",year),("$action",action),("$event",eventId)))
            decision.ExecuteNonQuery();
        using (var status=Cmd(con,tx, """
            UPDATE student_enrollments SET status=$status,updated_utc=$at
            WHERE student_id=$id AND school_id=$school AND academic_year=$year AND status='مسجل'
            """,("$status",action=="graduate"?"متخرج":"منسحب"),("$at",Utc()),
            ("$id",studentId),("$school",schoolId),("$year",year)))
            if(status.ExecuteNonQuery()!=1) throw new InvalidOperationException("Enrollment already completed.");
        using (var archive=Cmd(con,tx,
            "UPDATE school_students SET is_active=0,updated_utc=$at WHERE id=$id AND school_id=$school",
            ("$at",Utc()),("$id",studentId),("$school",schoolId))) archive.ExecuteNonQuery();
        Audit(con,tx,actor.Id,"student."+action,personId,$"school={schoolId};year={year}");
        tx.Commit();
    }
}
