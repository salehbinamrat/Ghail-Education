using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>
/// Phase 2 is additive. The original school_students/school_staff rows are preserved as
/// identity anchors; placements and assignments become separate historical entities.
/// </summary>
public sealed partial class SqliteStore
{
    private static void MigrateAcademicHistory(SqliteConnection con)
    {
        using (var cmd = Cmd(con, null, """
            CREATE TABLE IF NOT EXISTS academic_years(
                year TEXT PRIMARY KEY,
                is_active INTEGER NOT NULL DEFAULT 0 CHECK(is_active IN (0,1)),
                is_closed INTEGER NOT NULL DEFAULT 0 CHECK(is_closed IN (0,1)),
                opened_utc TEXT NOT NULL,
                closed_utc TEXT NULL,
                CHECK(is_active=0 OR is_closed=0)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_single_active_academic_year
                ON academic_years(is_active) WHERE is_active=1;
            CREATE TABLE IF NOT EXISTS student_school_codes(
                student_id TEXT NOT NULL REFERENCES school_students(id) ON DELETE RESTRICT,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                student_code TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(student_id,school_id),
                UNIQUE(school_id,student_code)
            );
            CREATE TABLE IF NOT EXISTS student_history(
                id TEXT PRIMARY KEY,
                student_id TEXT NOT NULL REFERENCES school_students(id) ON DELETE RESTRICT,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                academic_year TEXT NOT NULL REFERENCES academic_years(year) ON DELETE RESTRICT,
                grade_id TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('مسجل','منقول','منسحب','متخرج')),
                start_utc TEXT NOT NULL,
                end_utc TEXT NULL,
                FOREIGN KEY(grade_id,school_id,academic_year)
                    REFERENCES school_grades(id,school_id,academic_year) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_student_history_identity_year
                ON student_history(student_id,academic_year);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_student_active_per_year
                ON student_history(student_id,academic_year)
                WHERE end_utc IS NULL AND status='مسجل';
            CREATE INDEX IF NOT EXISTS ix_student_history_school_year
                ON student_history(school_id,academic_year,grade_id,end_utc);
            CREATE TABLE IF NOT EXISTS student_history_events(
                id TEXT PRIMARY KEY,
                student_id TEXT NOT NULL REFERENCES school_students(id) ON DELETE RESTRICT,
                previous_placement_id TEXT NULL REFERENCES student_history(id) ON DELETE RESTRICT,
                next_placement_id TEXT NULL REFERENCES student_history(id) ON DELETE RESTRICT,
                event_type TEXT NOT NULL,
                actor_id TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
                reason TEXT NOT NULL DEFAULT '',
                event_utc TEXT NOT NULL,
                UNIQUE(next_placement_id)
            );
            CREATE TABLE IF NOT EXISTS transferred_student_results(
                id TEXT PRIMARY KEY,
                student_id TEXT NOT NULL REFERENCES school_students(id) ON DELETE RESTRICT,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                academic_year TEXT NOT NULL REFERENCES academic_years(year) ON DELETE RESTRICT,
                term TEXT NOT NULL,
                subject TEXT NOT NULL COLLATE NOCASE,
                score INTEGER NOT NULL CHECK(score>=0),
                maximum INTEGER NOT NULL CHECK(maximum>0 AND score<=maximum),
                notes TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL,
                UNIQUE(student_id,school_id,academic_year,term,subject)
            );
            CREATE INDEX IF NOT EXISTS ix_transferred_results_student
                ON transferred_student_results(student_id,academic_year,school_id);
            CREATE TABLE IF NOT EXISTS transferred_student_attendance(
                id TEXT PRIMARY KEY,
                student_id TEXT NOT NULL REFERENCES school_students(id) ON DELETE RESTRICT,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                academic_year TEXT NOT NULL REFERENCES academic_years(year) ON DELETE RESTRICT,
                attendance_date TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('حاضر','غائب','متأخر','بعذر')),
                notes TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL,
                UNIQUE(student_id,school_id,attendance_date)
            );
            CREATE TABLE IF NOT EXISTS transferred_student_assessments(
                id TEXT PRIMARY KEY,
                student_id TEXT NOT NULL REFERENCES school_students(id) ON DELETE RESTRICT,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                academic_year TEXT NOT NULL REFERENCES academic_years(year) ON DELETE RESTRICT,
                category TEXT NOT NULL,
                rating TEXT NOT NULL,
                notes TEXT NOT NULL DEFAULT '',
                recorded_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS school_physical_rooms(
                id TEXT PRIMARY KEY,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                room_code TEXT NOT NULL COLLATE NOCASE,
                capacity INTEGER NOT NULL CHECK(capacity BETWEEN 1 AND 500),
                created_utc TEXT NOT NULL,
                UNIQUE(school_id,room_code),
                UNIQUE(id,school_id)
            );
            CREATE TABLE IF NOT EXISTS section_room_assignments(
                grade_id TEXT PRIMARY KEY REFERENCES school_grades(id) ON DELETE RESTRICT,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                academic_year TEXT NOT NULL REFERENCES academic_years(year) ON DELETE RESTRICT,
                room_id TEXT NOT NULL,
                shift TEXT NOT NULL CHECK(shift IN ('صباحي','مسائي','ليلي')),
                FOREIGN KEY(room_id,school_id) REFERENCES school_physical_rooms(id,school_id),
                UNIQUE(room_id,academic_year,shift)
            );
            CREATE TABLE IF NOT EXISTS school_infrastructure(
                school_id TEXT PRIMARY KEY REFERENCES schools(id) ON DELETE RESTRICT,
                geographic_area TEXT NOT NULL DEFAULT '',
                postal_address TEXT NOT NULL DEFAULT '',
                building_type TEXT NOT NULL DEFAULT '',
                building_ownership TEXT NOT NULL DEFAULT '',
                physical_rooms INTEGER NOT NULL DEFAULT 0 CHECK(physical_rooms>=0),
                operating_shifts TEXT NOT NULL DEFAULT '',
                has_water INTEGER NOT NULL DEFAULT 0 CHECK(has_water IN (0,1)),
                has_electricity INTEGER NOT NULL DEFAULT 0 CHECK(has_electricity IN (0,1)),
                has_internet INTEGER NOT NULL DEFAULT 0 CHECK(has_internet IN (0,1)),
                notes TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS school_facilities(
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                facility_name TEXT NOT NULL COLLATE NOCASE,
                quantity INTEGER NOT NULL CHECK(quantity >= 0),
                condition TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(school_id,facility_name)
            );
            CREATE TABLE IF NOT EXISTS employee_identities(
                id TEXT PRIMARY KEY REFERENCES school_staff(id) ON DELETE RESTRICT,
                employee_code TEXT NOT NULL COLLATE NOCASE,
                full_name TEXT NOT NULL,
                original_payroll_school_id TEXT NOT NULL REFERENCES schools(id),
                employment_category TEXT NOT NULL DEFAULT 'غير محدد',
                qualification TEXT NOT NULL DEFAULT '',
                specialization TEXT NOT NULL DEFAULT '',
                qualification_year INTEGER NULL,
                employment_year INTEGER NULL,
                contract_year INTEGER NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_employee_code ON employee_identities(employee_code);
            CREATE TABLE IF NOT EXISTS employee_assignments(
                id TEXT PRIMARY KEY,
                employee_id TEXT NOT NULL REFERENCES employee_identities(id) ON DELETE RESTRICT,
                payroll_school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                workplace_school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                assignment_type TEXT NOT NULL CHECK(assignment_type IN ('أصلي','انتداب','نقل','تكليف','إعارة','إجازة')),
                start_date TEXT NULL,
                end_date TEXT NULL,
                notes TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL,
                CHECK(start_date IS NULL OR end_date IS NULL OR start_date<=end_date)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_employee_active_assignment
                ON employee_assignments(employee_id) WHERE end_date IS NULL;
            CREATE INDEX IF NOT EXISTS ix_employee_workplace
                ON employee_assignments(workplace_school_id,end_date);
            """))
            cmd.ExecuteNonQuery();

        // Persist the stage that each class belonged to when the class was first
        // registered. School classification changes must not rewrite old transcripts.
        using (var stageColumn = Cmd(con, null,
            "SELECT COUNT(*) FROM pragma_table_info('school_grades') WHERE name='academic_stage'"))
        {
            if (Convert.ToInt32(stageColumn.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using var addStage = Cmd(con, null,
                    "ALTER TABLE school_grades ADD COLUMN academic_stage TEXT NOT NULL DEFAULT ''");
                addStage.ExecuteNonQuery();
            }
        }
        using (var fillStage = Cmd(con, null, """
            UPDATE school_grades SET academic_stage=(
                SELECT CASE WHEN s.is_kindergarten=1 THEN 'روضة'
                    WHEN s.stage='أساسي وثانوي' THEN 'مجمع تربوي'
                    ELSE s.stage END
                FROM schools s WHERE s.id=school_grades.school_id)
            WHERE academic_stage=''
            """)) fillStage.ExecuteNonQuery();
        // Original codes remain owned by their original school; transferred people
        // can get a different local code without changing their global student ID.
        using (var schoolCodes = Cmd(con, null, """
            INSERT OR IGNORE INTO student_school_codes(student_id,school_id,student_code)
            SELECT id,school_id,student_code FROM school_students
            """)) schoolCodes.ExecuteNonQuery();

        // Repeatable, lossless backfill from Phase 1. Historical years with no active
        // setting are closed instead of silently being made editable again.
        using (var years = Cmd(con, null, """
            INSERT OR IGNORE INTO academic_years(year,is_active,is_closed,opened_utc,closed_utc)
            SELECT year,
                   CASE WHEN year=(SELECT value FROM settings WHERE key='academic_year') THEN 1 ELSE 0 END,
                   CASE WHEN year=(SELECT value FROM settings WHERE key='academic_year') THEN 0 ELSE 1 END,
                   $at,NULL
            FROM (
                SELECT academic_year AS year FROM census_rounds
                UNION SELECT academic_year FROM school_grades
                UNION SELECT academic_year FROM student_enrollments
                UNION SELECT academic_year FROM student_results
                UNION SELECT academic_year FROM student_assessments
                UNION SELECT value FROM settings WHERE key='academic_year'
            ) WHERE year IS NOT NULL AND year <> ''
            """, ("$at", Utc())))
            years.ExecuteNonQuery();
        using (var legacy = Cmd(con, null, """
            INSERT INTO student_history(id,student_id,school_id,academic_year,grade_id,
                status,start_utc,end_utc)
            SELECT lower(hex(randomblob(16))), e.student_id,e.school_id,e.academic_year,
                e.grade_id,
                CASE WHEN e.status='مسجل' THEN 'مسجل' ELSE 'منسحب' END,
                e.updated_utc,
                CASE WHEN e.status='مسجل' THEN NULL ELSE e.updated_utc END
            FROM student_enrollments e
            WHERE NOT EXISTS (
                SELECT 1 FROM student_history h
                WHERE h.student_id=e.student_id AND h.academic_year=e.academic_year
            )
            """))
            legacy.ExecuteNonQuery();
        using (var people = Cmd(con, null, """
            INSERT OR IGNORE INTO employee_identities(id,employee_code,full_name,
                original_payroll_school_id,employment_category,qualification,specialization,
                qualification_year,employment_year,contract_year,updated_utc)
            SELECT st.id,st.employee_code,st.full_name,st.school_id,
                COALESCE(pd.employment_type,'غير محدد'),st.qualification,st.specialization,
                pd.qualification_year,pd.employment_year,pd.contract_year,st.updated_utc
            FROM school_staff st
            LEFT JOIN staff_personnel_details pd ON pd.staff_id=st.id
            """))
            people.ExecuteNonQuery();
        using (var assignments = Cmd(con, null, """
            INSERT INTO employee_assignments(id,employee_id,payroll_school_id,
                workplace_school_id,assignment_type,start_date,end_date,notes,created_utc)
            SELECT lower(hex(randomblob(16))),e.id,e.original_payroll_school_id,
                e.original_payroll_school_id,'أصلي',NULL,NULL,'سجل أصلي مرحل',e.updated_utc
            FROM employee_identities e
            WHERE NOT EXISTS(SELECT 1 FROM employee_assignments a WHERE a.employee_id=e.id)
            """))
            assignments.ExecuteNonQuery();
        // Explicit academic year on new attendance; legacy undated-year data remains
        // untouched (NULL) rather than guessing which year contained its calendar date.
        using (var check = Cmd(con, null,
            "SELECT COUNT(*) FROM pragma_table_info('student_attendance') WHERE name='academic_year'"))
        {
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using var alter = Cmd(con, null,
                    "ALTER TABLE student_attendance ADD COLUMN academic_year TEXT NULL REFERENCES academic_years(year)");
                alter.ExecuteNonQuery();
            }
        }
        using (var protect = Cmd(con, null, """
            CREATE TRIGGER IF NOT EXISTS p2_school_grade_insert BEFORE INSERT ON school_grades
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_school_grade_update BEFORE UPDATE ON school_grades
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_school_grade_delete BEFORE DELETE ON school_grades
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_enrollment_insert BEFORE INSERT ON student_enrollments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_enrollment_update BEFORE UPDATE ON student_enrollments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_enrollment_delete BEFORE DELETE ON student_enrollments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_history_insert BEFORE INSERT ON student_history
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_history_update BEFORE UPDATE ON student_history
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_history_delete BEFORE DELETE ON student_history
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_results_insert BEFORE INSERT ON student_results
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_results_update BEFORE UPDATE ON student_results
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_results_delete BEFORE DELETE ON student_results
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_assessments_insert BEFORE INSERT ON student_assessments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_assessments_update BEFORE UPDATE ON student_assessments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_assessments_delete BEFORE DELETE ON student_assessments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_attendance_insert BEFORE INSERT ON student_attendance
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_attendance_update BEFORE UPDATE ON student_attendance
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_attendance_delete BEFORE DELETE ON student_attendance
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_submissions_insert BEFORE INSERT ON submissions
            WHEN EXISTS(SELECT 1 FROM census_rounds r JOIN academic_years y ON y.year=r.academic_year
                WHERE r.id=NEW.round_id AND y.is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_submissions_update BEFORE UPDATE ON submissions
            WHEN EXISTS(SELECT 1 FROM census_rounds r JOIN academic_years y ON y.year=r.academic_year
                WHERE r.id=OLD.round_id AND y.is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_submissions_delete BEFORE DELETE ON submissions
            WHEN EXISTS(SELECT 1 FROM census_rounds r JOIN academic_years y ON y.year=r.academic_year
                WHERE r.id=OLD.round_id AND y.is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            -- A submission's child rows and approval history also form official records.
            CREATE TRIGGER IF NOT EXISTS p2_enrollment_rows_insert BEFORE INSERT ON enrollment_rows
            WHEN EXISTS(SELECT 1 FROM submissions s JOIN census_rounds r ON r.id=s.round_id
                JOIN academic_years y ON y.year=r.academic_year
                WHERE s.id=NEW.submission_id AND y.is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_enrollment_rows_update BEFORE UPDATE ON enrollment_rows
            WHEN EXISTS(SELECT 1 FROM submissions s JOIN census_rounds r ON r.id=s.round_id
                JOIN academic_years y ON y.year=r.academic_year
                WHERE s.id=OLD.submission_id AND y.is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_enrollment_rows_delete BEFORE DELETE ON enrollment_rows
            WHEN EXISTS(SELECT 1 FROM submissions s JOIN census_rounds r ON r.id=s.round_id
                JOIN academic_years y ON y.year=r.academic_year
                WHERE s.id=OLD.submission_id AND y.is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_decisions_insert BEFORE INSERT ON decisions
            WHEN EXISTS(SELECT 1 FROM submissions s JOIN census_rounds r ON r.id=s.round_id
                JOIN academic_years y ON y.year=r.academic_year
                WHERE s.id=NEW.submission_id AND y.is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_decisions_update BEFORE UPDATE ON decisions
            WHEN EXISTS(SELECT 1 FROM submissions s JOIN census_rounds r ON r.id=s.round_id
                JOIN academic_years y ON y.year=r.academic_year
                WHERE s.id=OLD.submission_id AND y.is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_decisions_delete BEFORE DELETE ON decisions
            WHEN EXISTS(SELECT 1 FROM submissions s JOIN census_rounds r ON r.id=s.round_id
                JOIN academic_years y ON y.year=r.academic_year
                WHERE s.id=OLD.submission_id AND y.is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_sections_room_insert BEFORE INSERT ON section_room_assignments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_sections_room_update BEFORE UPDATE ON section_room_assignments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_sections_room_delete BEFORE DELETE ON section_room_assignments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_student_created AFTER INSERT ON school_students
            BEGIN
                SELECT 1;
            END;
            CREATE TRIGGER IF NOT EXISTS p2_student_school_code_created AFTER INSERT ON school_students
            BEGIN
                INSERT INTO student_school_codes(student_id,school_id,student_code)
                VALUES(NEW.id,NEW.school_id,NEW.student_code);
            END;
            CREATE TRIGGER IF NOT EXISTS p2_student_school_code_updated AFTER UPDATE OF student_code ON school_students
            BEGIN
                UPDATE student_school_codes SET student_code=NEW.student_code
                WHERE student_id=NEW.id AND school_id=NEW.school_id;
            END;
            CREATE TRIGGER IF NOT EXISTS p2_grade_snapshot_created AFTER INSERT ON school_grades
            BEGIN
                UPDATE school_grades SET academic_stage=(
                    SELECT CASE WHEN s.is_kindergarten=1 THEN 'روضة'
                        WHEN s.stage='أساسي وثانوي' THEN 'مجمع تربوي' ELSE s.stage END
                    FROM schools s WHERE s.id=NEW.school_id)
                WHERE id=NEW.id AND academic_stage='';
            END;
            CREATE TRIGGER IF NOT EXISTS p2_enrollment_conflicts BEFORE INSERT ON student_enrollments
            WHEN EXISTS (
                SELECT 1 FROM student_history h
                WHERE h.student_id=NEW.student_id AND h.academic_year=NEW.academic_year
                  AND NOT EXISTS (
                    SELECT 1 FROM student_history current
                    WHERE current.student_id=NEW.student_id AND current.academic_year=NEW.academic_year
                      AND current.school_id=NEW.school_id
                      AND current.end_utc IS NULL AND current.status='مسجل'
                  )
            )
            BEGIN SELECT RAISE(ABORT,'Use controlled enrollment transfer; history already exists'); END;
            CREATE TRIGGER IF NOT EXISTS p2_enrollment_created AFTER INSERT ON student_enrollments
            BEGIN
                INSERT INTO student_history(id,student_id,school_id,academic_year,grade_id,
                    status,start_utc,end_utc)
                SELECT lower(hex(randomblob(16))),NEW.student_id,NEW.school_id,NEW.academic_year,
                    NEW.grade_id,CASE WHEN NEW.status='مسجل' THEN 'مسجل' ELSE 'منسحب' END,
                    NEW.updated_utc,CASE WHEN NEW.status='مسجل' THEN NULL ELSE NEW.updated_utc END
                WHERE NOT EXISTS (
                    SELECT 1 FROM student_history h WHERE h.student_id=NEW.student_id
                    AND h.academic_year=NEW.academic_year
                );
                UPDATE student_history SET grade_id=NEW.grade_id
                WHERE student_id=NEW.student_id AND school_id=NEW.school_id
                  AND academic_year=NEW.academic_year AND end_utc IS NULL AND status='مسجل';
            END;
            CREATE TRIGGER IF NOT EXISTS p2_enrollment_changed AFTER UPDATE OF grade_id ON student_enrollments
            BEGIN
                UPDATE student_history SET grade_id=NEW.grade_id
                WHERE student_id=NEW.student_id AND school_id=NEW.school_id
                  AND academic_year=NEW.academic_year AND end_utc IS NULL AND status='مسجل';
            END;
            CREATE TRIGGER IF NOT EXISTS p2_transfer_results_insert BEFORE INSERT ON transferred_student_results
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_transfer_results_update BEFORE UPDATE ON transferred_student_results
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_transfer_results_delete BEFORE DELETE ON transferred_student_results
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_transfer_attendance_insert BEFORE INSERT ON transferred_student_attendance
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_transfer_attendance_update BEFORE UPDATE ON transferred_student_attendance
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_transfer_attendance_delete BEFORE DELETE ON transferred_student_attendance
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_transfer_assessments_insert BEFORE INSERT ON transferred_student_assessments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=NEW.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_transfer_assessments_update BEFORE UPDATE ON transferred_student_assessments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_transfer_assessments_delete BEFORE DELETE ON transferred_student_assessments
            WHEN EXISTS(SELECT 1 FROM academic_years WHERE year=OLD.academic_year AND is_closed=1)
            BEGIN SELECT RAISE(ABORT,'Academic year closed'); END;
            CREATE TRIGGER IF NOT EXISTS p2_staff_created AFTER INSERT ON school_staff
            BEGIN
                INSERT OR IGNORE INTO employee_identities(id,employee_code,full_name,
                    original_payroll_school_id,employment_category,qualification,specialization,updated_utc)
                VALUES(NEW.id,NEW.employee_code,NEW.full_name,NEW.school_id,
                    'غير محدد',NEW.qualification,NEW.specialization,NEW.updated_utc);
                INSERT INTO employee_assignments(id,employee_id,payroll_school_id,
                    workplace_school_id,assignment_type,start_date,end_date,notes,created_utc)
                VALUES(lower(hex(randomblob(16))),NEW.id,NEW.school_id,NEW.school_id,
                    'أصلي',NULL,NULL,'سجل أصلي',NEW.created_utc);
            END;
            CREATE TRIGGER IF NOT EXISTS p2_staff_updated AFTER UPDATE ON school_staff
            BEGIN
                UPDATE employee_identities SET employee_code=NEW.employee_code,
                    full_name=NEW.full_name,qualification=NEW.qualification,
                    specialization=NEW.specialization,updated_utc=NEW.updated_utc
                WHERE id=NEW.id;
            END;
            """))
            protect.ExecuteNonQuery();
    }

    private static void CheckYearFormat(string year)
    {
        if (string.IsNullOrWhiteSpace(year) ||
            !Regex.IsMatch(year, @"^[0-9]{4}/[0-9]{4}$", RegexOptions.CultureInvariant) ||
            int.Parse(year[..4], CultureInfo.InvariantCulture) is < 1900 or > 2099 ||
            int.Parse(year[5..], CultureInfo.InvariantCulture) !=
            int.Parse(year[..4], CultureInfo.InvariantCulture) + 1)
            throw new ArgumentException("العام الدراسي يجب أن يكون مثل 2027/2028.");
    }

    private static void RequireYearOwner(UserAccount actor)
    {
        if (!actor.IsOwner) throw new UnauthorizedAccessException(
            "إدارة انتقالات السنوات الدراسية خاصة بالمسؤول المؤسس.");
    }

    private static void RequireOpenYear(SqliteConnection con, SqliteTransaction? tx, string year)
    {
        using var cmd = Cmd(con, tx,
            "SELECT EXISTS(SELECT 1 FROM academic_years WHERE year=$y AND is_closed=0)",
            ("$y", year));
        if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("العام الدراسي مغلق أو غير موجود.");
    }

    public IReadOnlyList<AcademicYearRecord> GetAcademicYears(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null,
            "SELECT year,is_active,is_closed,opened_utc,closed_utc FROM academic_years ORDER BY year");
        using var reader = cmd.ExecuteReader();
        var rows = new List<AcademicYearRecord>();
        while (reader.Read())
            rows.Add(new AcademicYearRecord(Text(reader, 0), reader.GetInt32(1) == 1,
                reader.GetInt32(2) == 1, Text(reader, 3), NullableText(reader, 4)));
        return rows;
    }

    public void OpenAcademicYear(UserAccount actor, string year)
    {
        RequireFreshActor(actor);
        RequireYearOwner(actor);
        CheckYearFormat(year);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var last = Cmd(con, tx, "SELECT MAX(year) FROM academic_years"))
        {
            var latest = last.ExecuteScalar()?.ToString();
            if (!string.IsNullOrEmpty(latest) &&
                string.CompareOrdinal(year, latest) <= 0)
                throw new InvalidOperationException("يجب فتح عام دراسي جديد بعد أحدث عام محفوظ.");
        }
        using (var cmd = Cmd(con, tx,
            "INSERT INTO academic_years(year,is_active,is_closed,opened_utc) VALUES($y,0,0,$at)",
            ("$y", year), ("$at", Utc()))) cmd.ExecuteNonQuery();
        using (var cmd = Cmd(con, tx, """
            INSERT INTO census_rounds(id,academic_year,snapshot_date,is_active)
            VALUES($id,$y,$date,0)
            ON CONFLICT(academic_year) DO NOTHING
            """, ("$id", Id()), ("$y", year),
            ("$date", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))
            cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "academic_year.open", year, "New academic year opened");
        tx.Commit();
    }

    public void ActivateAcademicYear(UserAccount actor, string year)
    {
        RequireFreshActor(actor);
        RequireYearOwner(actor);
        CheckYearFormat(year);
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireOpenYear(con, tx, year);
        using (var current = Cmd(con, tx, "UPDATE academic_years SET is_active=0 WHERE is_active=1"))
            current.ExecuteNonQuery();
        using (var next = Cmd(con, tx, "UPDATE academic_years SET is_active=1 WHERE year=$y",
            ("$y", year))) next.ExecuteNonQuery();
        using (var setting = Cmd(con, tx, """
            INSERT INTO settings(key,value) VALUES('academic_year',$y)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """, ("$y", year))) setting.ExecuteNonQuery();
        using (var oldRound = Cmd(con, tx, "UPDATE census_rounds SET is_active=0 WHERE is_active=1"))
            oldRound.ExecuteNonQuery();
        using (var nextRound = Cmd(con, tx, "UPDATE census_rounds SET is_active=1 WHERE academic_year=$y",
            ("$y", year))) nextRound.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "academic_year.activate", year, "Active year changed without modifying historical records");
        tx.Commit();
    }

    public void CloseAcademicYear(UserAccount actor, string year)
    {
        RequireFreshActor(actor);
        RequireYearOwner(actor);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = Cmd(con, tx, """
            UPDATE academic_years SET is_closed=1,closed_utc=$at
            WHERE year=$y AND is_closed=0 AND is_active=0
            """, ("$y", year), ("$at", Utc()));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("لا يمكن إغلاق عام نشط أو إغلاق عام مغلق سابقا.");
        Audit(con, tx, actor.Id, "academic_year.close", year, "Official historical records locked");
        tx.Commit();
    }

    private static bool CanSeePhase2Student(UserAccount actor, string schoolId) =>
        AccessPolicy.CanSeeStudents(actor, schoolId);

    public IReadOnlyList<StudentEnrollmentHistoryRecord> GetStudentEnrollmentHistory(
        UserAccount actor, string studentId)
    {
        RequireFreshActor(actor);
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT h.id,h.student_id,h.school_id,h.academic_year,g.grade,g.section,
                h.status,h.start_utc,h.end_utc
            FROM student_history h JOIN school_grades g ON g.id=h.grade_id
            WHERE h.student_id=$id ORDER BY h.academic_year,h.start_utc,h.id
            """, ("$id", studentId));
        using var r = cmd.ExecuteReader();
        var rows = new List<StudentEnrollmentHistoryRecord>();
        while (r.Read())
        {
            var school = Text(r, 2);
            if (CanSeePhase2Student(actor, school))
                rows.Add(new StudentEnrollmentHistoryRecord(Text(r, 0), Text(r, 1),
                    school, Text(r, 3), r.GetInt32(4), Text(r, 5),
                    Text(r, 6), Text(r, 7), NullableText(r, 8)));
        }
        if (rows.Count == 0) throw new UnauthorizedAccessException(
            "السجل الدراسي غير متاح ضمن صلاحيات هذا المستخدم.");
        return rows;
    }

    private void MoveStudent(UserAccount actor, string studentId, string targetSchool,
        int grade, string section, string action, bool repeat = false)
    {
        RequireFreshActor(actor);
        section = SectionName(section);
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireOpenYear(con, tx, year);
        using var source = Cmd(con, tx, """
            SELECT h.id,h.school_id,h.academic_year,g.grade,g.section
            FROM student_history h JOIN school_grades g ON g.id=h.grade_id
            WHERE h.student_id=$student AND h.end_utc IS NULL AND h.status='مسجل'
            ORDER BY h.academic_year DESC,h.start_utc DESC,h.id DESC LIMIT 1
            """, ("$student", studentId));
        string beforeId, sourceSchool, sourceYear, oldSection;
        int oldGrade;
        using (var r = source.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException("لا يوجد قيد نشط للطالب.");
            beforeId = Text(r, 0); sourceSchool = Text(r, 1);
            sourceYear = Text(r, 2); oldGrade = r.GetInt32(3); oldSection = Text(r, 4);
        }
        if (!AccessPolicy.CanManageStudents(actor, sourceSchool) ||
            !AccessPolicy.CanManageStudents(actor, targetSchool))
            throw new UnauthorizedAccessException("يتطلب النقل صلاحية المدرستين المصدر والوجهة.");
        if (action == "ترفيع")
        {
            using (var advanced = Cmd(con, tx, """
                SELECT EXISTS(SELECT 1 FROM student_history
                WHERE student_id=$student AND academic_year=$year)
                """, ("$student", studentId), ("$year", year)))
            {
                if (Convert.ToInt32(advanced.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    throw new InvalidOperationException("سبق إنشاء قيد للطالب في العام المستهدف؛ الترفيع لا يكرر.");
            }
            if (string.CompareOrdinal(sourceYear, year) >= 0 ||
                grade != oldGrade + (repeat ? 0 : 1))
                throw new InvalidOperationException("الترفيع يتطلب عاما لاحقا وصفا مطابقا للنجاح أو الإعادة.");
        }
        else if (action == "شعبة")
        {
            if (sourceYear != year || targetSchool != sourceSchool ||
                grade != oldGrade || section == oldSection)
                throw new InvalidOperationException("تغيير الشعبة مسموح داخل المدرسة والصف والعام نفسه.");
        }
        else if (action == "مدرسة")
        {
            if (targetSchool == sourceSchool)
                throw new InvalidOperationException("اختر مدرسة مختلفة للنقل.");
        }
        else throw new ArgumentException("نوع انتقال الطالب غير مدعوم.");
        var school = StudentSchool(con, tx, targetSchool);
        GradeAllowed(school, grade);
        using (var identity = Cmd(con, tx, "SELECT gender FROM school_students WHERE id=$student",
            ("$student", studentId)))
        {
            var gender = identity.ExecuteScalar()?.ToString()
                ?? throw new InvalidOperationException("هوية الطالب غير موجودة.");
            _ = StudentGender(school, gender);
        }
        var gradeId = GradeId(con, tx, targetSchool, year, grade, section);
        // Local student codes belong to the receiving school, while studentId is
        // immutable. Preserve the previous code at the originating institution.
        using (var local = Cmd(con, tx, """
            SELECT student_code FROM student_school_codes
            WHERE student_id=$student AND school_id=$school
            """, ("$student", studentId), ("$school", targetSchool)))
        {
            if (local.ExecuteScalar() is null)
            {
                using var sourceCode = Cmd(con, tx,
                    "SELECT student_code FROM school_students WHERE id=$id", ("$id", studentId));
                var suggestion = sourceCode.ExecuteScalar()?.ToString()
                    ?? throw new InvalidOperationException("هوية الطالب غير موجودة.");
                using var collision = Cmd(con, tx, """
                    SELECT EXISTS(SELECT 1 FROM student_school_codes
                    WHERE school_id=$school AND student_code=$code)
                    """, ("$school", targetSchool), ("$code", suggestion));
                if (Convert.ToInt32(collision.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    suggestion = "TR-" + Id()[..16].ToUpperInvariant();
                using var assignCode = Cmd(con, tx, """
                    INSERT INTO student_school_codes(student_id,school_id,student_code)
                    VALUES($student,$school,$code)
                    """, ("$student", studentId), ("$school", targetSchool), ("$code", suggestion));
                assignCode.ExecuteNonQuery();
            }
        }
        using (var existing = Cmd(con, tx, """
            SELECT EXISTS(SELECT 1 FROM student_history
                WHERE student_id=$student AND academic_year=$year
                AND end_utc IS NULL AND status='مسجل')
            """, ("$student", studentId), ("$year", year)))
        {
            var has = Convert.ToInt32(existing.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
            if (has && sourceYear != year)
                throw new InvalidOperationException("تم تسجيل أو ترفيع الطالب في هذا العام سابقا.");
        }
        if (sourceYear == year)
        {
            using var retire = Cmd(con, tx, """
                UPDATE student_history SET end_utc=$at,status='منقول'
                WHERE id=$id AND end_utc IS NULL
                """, ("$at", Utc()), ("$id", beforeId));
            if (retire.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("تعذر إغلاق القيد السابق.");
        }
        var nextId = Id();
        using (var insert = Cmd(con, tx, """
            INSERT INTO student_history(id,student_id,school_id,academic_year,
                grade_id,status,start_utc,end_utc)
            VALUES($id,$student,$school,$year,$grade,'مسجل',$at,NULL)
            """, ("$id", nextId), ("$student", studentId), ("$school", targetSchool),
            ("$year", year), ("$grade", gradeId), ("$at", Utc())))
            insert.ExecuteNonQuery();
        using (var ev = Cmd(con, tx, """
            INSERT INTO student_history_events(id,student_id,previous_placement_id,
                next_placement_id,event_type,actor_id,event_utc)
            VALUES($id,$student,$old,$next,$type,$actor,$at)
            """, ("$id", Id()), ("$student", studentId), ("$old", beforeId),
            ("$next", nextId), ("$type", repeat ? "إعادة" : action),
            ("$actor", actor.Id), ("$at", Utc()))) ev.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "student.transition", studentId,
            $"action={action};source={sourceSchool};target={targetSchool};year={year};grade={grade}");
        tx.Commit();
    }

    public void PromoteStudent(UserAccount actor, string studentId, string schoolId,
        int grade, string section, bool repeat) =>
        MoveStudent(actor, studentId, schoolId, grade, section, "ترفيع", repeat);

    public void TransferStudentSection(UserAccount actor, string studentId,
        string schoolId, int grade, string section) =>
        MoveStudent(actor, studentId, schoolId, grade, section, "شعبة");

    public void TransferStudentSchool(UserAccount actor, string studentId,
        string targetSchoolId, int grade, string section) =>
        MoveStudent(actor, studentId, targetSchoolId, grade, section, "مدرسة");

    private void EndStudentPlacement(UserAccount actor, string studentId, string eventName)
    {
        RequireFreshActor(actor);
        var year = AcademicYear;
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireOpenYear(con, tx, year);
        using var get = Cmd(con, tx, """
            SELECT id,school_id FROM student_history WHERE student_id=$id AND academic_year=$year
            AND end_utc IS NULL AND status='مسجل'
            """, ("$id", studentId), ("$year", year));
        string placement, school;
        using (var r = get.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException("الطالب ليس مقيدا بالعام النشط.");
            placement = Text(r, 0); school = Text(r, 1);
        }
        if (!AccessPolicy.CanManageStudents(actor, school)) throw new UnauthorizedAccessException();
        using (var end = Cmd(con, tx,
            "UPDATE student_history SET end_utc=$at,status=$status WHERE id=$id AND end_utc IS NULL",
            ("$id", placement), ("$at", Utc()), ("$status", eventName)))
        {
            if (end.ExecuteNonQuery() != 1) throw new InvalidOperationException();
        }
        using (var ev = Cmd(con, tx, """
            INSERT INTO student_history_events(id,student_id,previous_placement_id,
                next_placement_id,event_type,actor_id,event_utc)
            VALUES($id,$student,$old,NULL,$type,$actor,$at)
            """, ("$id", Id()), ("$student", studentId), ("$old", placement),
            ("$type", eventName), ("$actor", actor.Id), ("$at", Utc()))) ev.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "student.archive", studentId, $"status={eventName};year={year}");
        tx.Commit();
    }

    public void GraduateStudent(UserAccount actor, string studentId) =>
        EndStudentPlacement(actor, studentId, "متخرج");

    public void WithdrawStudent(UserAccount actor, string studentId) =>
        EndStudentPlacement(actor, studentId, "منسحب");

    public PhysicalClassroom AddPhysicalClassroom(UserAccount actor, string schoolId,
        string code, int capacity)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudents(actor, schoolId)) throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(code) || code.Length > 80 || capacity is < 1 or > 500)
            throw new ArgumentException("بيانات الغرفة غير صحيحة.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        _ = StudentSchool(con, tx, schoolId);
        var id = Id();
        using (var cmd = Cmd(con, tx, """
            INSERT INTO school_physical_rooms(id,school_id,room_code,capacity,created_utc)
            VALUES($id,$s,$code,$capacity,$at)
            """, ("$id", id), ("$s", schoolId), ("$code", code.Trim()),
            ("$capacity", capacity), ("$at", Utc()))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "school.room", id, $"school={schoolId}");
        tx.Commit();
        return new PhysicalClassroom(id, schoolId, code.Trim(), capacity);
    }

    public IReadOnlyList<PhysicalClassroom> GetPhysicalClassrooms(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null,
            "SELECT id,school_id,room_code,capacity FROM school_physical_rooms WHERE school_id=$s ORDER BY room_code",
            ("$s", schoolId));
        using var r = cmd.ExecuteReader();
        var rows = new List<PhysicalClassroom>();
        while (r.Read()) rows.Add(new PhysicalClassroom(Text(r, 0), Text(r, 1),
            Text(r, 2), r.GetInt32(3)));
        return rows;
    }

    public void AssignSectionRoom(UserAccount actor, string gradeId, string roomId, string shift)
    {
        RequireFreshActor(actor);
        if (shift is not ("صباحي" or "مسائي" or "ليلي")) throw new ArgumentException("الفترة غير صالحة.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var grade = Cmd(con, tx,
            "SELECT school_id,academic_year FROM school_grades WHERE id=$id",
            ("$id", gradeId));
        string school, year;
        using (var r = grade.ExecuteReader())
        {
            if (!r.Read()) throw new ArgumentException("الصف غير موجود.");
            school = Text(r, 0); year = Text(r, 1);
        }
        if (!AccessPolicy.CanManageStudents(actor, school)) throw new UnauthorizedAccessException();
        RequireOpenYear(con, tx, year);
        using (var cmd = Cmd(con, tx, """
            INSERT INTO section_room_assignments(grade_id,school_id,academic_year,room_id,shift)
            VALUES($grade,$school,$year,$room,$shift)
            ON CONFLICT(grade_id) DO UPDATE SET room_id=excluded.room_id,shift=excluded.shift
            """, ("$grade", gradeId), ("$school", school), ("$year", year),
            ("$room", roomId), ("$shift", shift))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "school.room_assignment", gradeId, $"room={roomId};shift={shift}");
        tx.Commit();
    }

    public IReadOnlyList<SectionRoomAssignment> GetSectionRoomAssignments(UserAccount actor,
        string schoolId, string year)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT grade_id,room_id,academic_year,school_id,shift
            FROM section_room_assignments WHERE school_id=$s AND academic_year=$year
            ORDER BY grade_id
            """, ("$s", schoolId), ("$year", year));
        using var r = cmd.ExecuteReader();
        var rows = new List<SectionRoomAssignment>();
        while (r.Read()) rows.Add(new SectionRoomAssignment(Text(r, 0), Text(r, 1),
            Text(r, 2), Text(r, 3), Text(r, 4)));
        return rows;
    }

    public SchoolInfrastructureRecord GetSchoolInfrastructure(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeSchool(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT geographic_area,postal_address,building_type,building_ownership,
                physical_rooms,operating_shifts,has_water,has_electricity,has_internet,notes
            FROM school_infrastructure WHERE school_id=$s
            """, ("$s", schoolId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new SchoolInfrastructureRecord(schoolId, "", "", "", "",
            0, "", false, false, false, "");
        return new SchoolInfrastructureRecord(schoolId, Text(r, 0), Text(r, 1),
            Text(r, 2), Text(r, 3), r.GetInt32(4), Text(r, 5), r.GetInt32(6) == 1,
            r.GetInt32(7) == 1, r.GetInt32(8) == 1, Text(r, 9));
    }

    public void SaveSchoolInfrastructure(UserAccount actor, SchoolInfrastructureRecord data)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditSchoolProfile(actor, data.SchoolId))
            throw new UnauthorizedAccessException();
        if (data.PhysicalRooms is < 0 or > 10000 ||
            new[] { data.GeographicArea, data.PostalAddress, data.BuildingType,
                data.BuildingOwnership, data.OperatingShifts }.Any(s => s.Length > 300) ||
            data.Notes.Length > 2500)
            throw new ArgumentException("ملف مرافق المدرسة غير صالح.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var cmd = Cmd(con, tx, """
            INSERT INTO school_infrastructure(school_id,geographic_area,postal_address,
                building_type,building_ownership,physical_rooms,operating_shifts,
                has_water,has_electricity,has_internet,notes,updated_utc)
            VALUES($s,$area,$address,$type,$ownership,$rooms,$shifts,$water,$power,$net,$notes,$at)
            ON CONFLICT(school_id) DO UPDATE SET geographic_area=excluded.geographic_area,
                postal_address=excluded.postal_address,building_type=excluded.building_type,
                building_ownership=excluded.building_ownership,physical_rooms=excluded.physical_rooms,
                operating_shifts=excluded.operating_shifts,has_water=excluded.has_water,
                has_electricity=excluded.has_electricity,has_internet=excluded.has_internet,
                notes=excluded.notes,updated_utc=excluded.updated_utc
            """, ("$s", data.SchoolId), ("$area", data.GeographicArea.Trim()),
            ("$address", data.PostalAddress.Trim()), ("$type", data.BuildingType.Trim()),
            ("$ownership", data.BuildingOwnership.Trim()), ("$rooms", data.PhysicalRooms),
            ("$shifts", data.OperatingShifts.Trim()), ("$water", data.HasWater ? 1 : 0),
            ("$power", data.HasElectricity ? 1 : 0), ("$net", data.HasInternet ? 1 : 0),
            ("$notes", data.Notes.Trim()), ("$at", Utc()))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "school.infrastructure", data.SchoolId, "School facilities updated");
        tx.Commit();
    }

    public void SaveSchoolFacility(UserAccount actor, SchoolFacility facility)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditSchoolProfile(actor, facility.SchoolId))
            throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(facility.Name) || facility.Name.Length > 120 ||
            facility.Quantity is < 0 or > 100000 || facility.Condition.Length > 300)
            throw new ArgumentException("بيانات المرفق غير صحيحة.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var cmd = Cmd(con, tx, """
            INSERT INTO school_facilities(school_id,facility_name,quantity,condition,updated_utc)
            VALUES($s,$name,$quantity,$condition,$at)
            ON CONFLICT(school_id,facility_name) DO UPDATE
            SET quantity=excluded.quantity,condition=excluded.condition,updated_utc=excluded.updated_utc
            """, ("$s", facility.SchoolId), ("$name", facility.Name.Trim()),
            ("$quantity", facility.Quantity), ("$condition", facility.Condition.Trim()),
            ("$at", Utc()))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "school.facility", facility.SchoolId, facility.Name);
        tx.Commit();
    }

    public IReadOnlyList<SchoolFacility> GetSchoolFacilities(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeSchool(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null,
            "SELECT school_id,facility_name,quantity,condition FROM school_facilities WHERE school_id=$s ORDER BY facility_name",
            ("$s", schoolId));
        using var r = cmd.ExecuteReader();
        var rows = new List<SchoolFacility>();
        while (r.Read()) rows.Add(new SchoolFacility(Text(r, 0), Text(r, 1),
            r.GetInt32(2), Text(r, 3)));
        return rows;
    }

    public IReadOnlyList<EmployeeAssignmentRecord> GetEmployeeAssignments(UserAccount actor, string employeeId)
    {
        RequireFreshActor(actor);
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT id,employee_id,payroll_school_id,workplace_school_id,
                assignment_type,start_date,end_date,notes
            FROM employee_assignments WHERE employee_id=$id
            ORDER BY COALESCE(start_date,''),created_utc,id
            """, ("$id", employeeId));
        using var r = cmd.ExecuteReader();
        var rows = new List<EmployeeAssignmentRecord>();
        while (r.Read())
        {
            var payroll = Text(r, 2);
            var workplace = Text(r, 3);
            // No disclosure of assignments to another school's operator.
            if (!AccessPolicy.CanSeeStaff(actor, payroll) ||
                !AccessPolicy.CanSeeStaff(actor, workplace)) continue;
            rows.Add(new EmployeeAssignmentRecord(Text(r, 0), Text(r, 1), payroll, workplace,
                Text(r, 4), NullableText(r, 5), NullableText(r, 6), Text(r, 7)));
        }
        if (rows.Count == 0) throw new UnauthorizedAccessException();
        return rows;
    }

    public void AssignEmployee(UserAccount actor, string employeeId, string workSchoolId,
        string assignmentType, DateOnly start, DateOnly? end, string notes = "")
    {
        RequireFreshActor(actor);
        if (assignmentType is not ("أصلي" or "انتداب" or "نقل" or "تكليف" or "إعارة" or "إجازة") ||
            (end.HasValue && end.Value < start) || notes.Length > 1000)
            throw new ArgumentException("نوع أو مدة التكليف غير صحيح.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        string payroll;
        using (var lookup = Cmd(con, tx,
            "SELECT original_payroll_school_id FROM employee_identities WHERE id=$id",
            ("$id", employeeId)))
            payroll = lookup.ExecuteScalar()?.ToString() ??
                throw new ArgumentException("هوية الموظف غير موجودة.");
        if (!AccessPolicy.CanEditStaff(actor, payroll) ||
            !AccessPolicy.CanEditStaff(actor, workSchoolId))
            throw new UnauthorizedAccessException("صلاحية المدرستين مطلوبة للنقل والانتداب.");
        var when = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // Intervals are inclusive. Ended assignments can be followed by another
        // assignment, but never overlapped or retroactively reordered.
        using (var overlap = Cmd(con, tx, """
            SELECT EXISTS(SELECT 1 FROM employee_assignments
               WHERE employee_id=$id
                 AND (
                    (start_date IS NOT NULL AND start_date >= $start
                       AND ($end IS NULL OR start_date <= $end))
                    OR (end_date IS NOT NULL AND end_date >= $start
                       AND (start_date IS NULL OR $end IS NULL OR start_date <= $end))
                 ))
            """, ("$id", employeeId), ("$start", when),
            ("$end", end?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))
        {
            if (Convert.ToInt32(overlap.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("تتعارض مدة هذا التكليف مع تكليف موجود.");
        }
        // The original imported/created assignment can have an unknown start
        // date. Close it at the day before the first explicit placement.
        using (var close = Cmd(con, tx, """
            UPDATE employee_assignments SET end_date=$previous
            WHERE employee_id=$id AND end_date IS NULL
              AND (start_date IS NULL OR start_date<$start)
            """, ("$previous", start.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$id", employeeId), ("$start", when)))
            close.ExecuteNonQuery();
        using (var insert = Cmd(con, tx, """
            INSERT INTO employee_assignments(id,employee_id,payroll_school_id,
                workplace_school_id,assignment_type,start_date,end_date,notes,created_utc)
            VALUES($id,$employee,$payroll,$work,$type,$start,$end,$notes,$at)
            """, ("$id", Id()), ("$employee", employeeId), ("$payroll", payroll),
            ("$work", workSchoolId), ("$type", assignmentType), ("$start", when),
            ("$end", end?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$notes", notes.Trim()), ("$at", Utc()))) insert.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "employee.assignment", employeeId,
            $"payroll={payroll};workplace={workSchoolId};kind={assignmentType};start={when}");
        tx.Commit();
    }

    public EmployeeCareerRecord GetEmployeeCareer(UserAccount actor, string employeeId)
    {
        RequireFreshActor(actor);
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT id,employee_code,full_name,employment_category,qualification,
                specialization,qualification_year,employment_year,contract_year,
                original_payroll_school_id FROM employee_identities WHERE id=$id
            """, ("$id", employeeId));
        using var r = cmd.ExecuteReader();
        if (!r.Read() || !AccessPolicy.CanSeeStaff(actor, Text(r, 9)))
            throw new UnauthorizedAccessException();
        return new EmployeeCareerRecord(Text(r, 0), Text(r, 1), Text(r, 2),
            Text(r, 3), Text(r, 4), Text(r, 5),
            r.IsDBNull(6) ? null : r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetInt32(7),
            r.IsDBNull(8) ? null : r.GetInt32(8), Text(r, 9));
    }

    public void SaveEmployeeCareer(UserAccount actor, EmployeeCareerRecord record)
    {
        RequireFreshActor(actor);
        var original = GetEmployeeCareer(actor, record.EmployeeId);
        if (!AccessPolicy.CanEditStaff(actor, original.OriginalPayrollSchoolId))
            throw new UnauthorizedAccessException();
        if (record.OriginalPayrollSchoolId != original.OriginalPayrollSchoolId)
            throw new InvalidOperationException("مدرسة الراتب الأصلية ثابتة؛ يغير التكليف مكان العمل فقط.");
        foreach (var year in new[] { record.QualificationYear, record.EmploymentYear, record.ContractYear })
            if (year is < 1900 or > 2100) throw new ArgumentException("سنة التأهيل أو التوظيف غير صحيحة.");
        if (record.EmploymentCategory.Length > 80 || record.Qualification.Length > 120 ||
            record.Specialization.Length > 120)
            throw new ArgumentException("البيانات الوظيفية طويلة جدا.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var cmd = Cmd(con, tx, """
            UPDATE employee_identities SET employment_category=$category,
               qualification=$qualification,specialization=$specialization,
               qualification_year=$qyear,employment_year=$eyear,
               contract_year=$cyear,updated_utc=$at WHERE id=$id
            """, ("$category", record.EmploymentCategory.Trim()),
            ("$qualification", record.Qualification.Trim()), ("$specialization", record.Specialization.Trim()),
            ("$qyear", record.QualificationYear), ("$eyear", record.EmploymentYear),
            ("$cyear", record.ContractYear), ("$at", Utc()), ("$id", record.EmployeeId)))
            cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "employee.career", record.EmployeeId, "Career metadata updated");
        tx.Commit();
    }
}
