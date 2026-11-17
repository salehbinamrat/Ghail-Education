using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    private static void MigrateParentCouncils(SqliteConnection con)
    {
        using var cmd = Cmd(con, null, """
            CREATE TABLE IF NOT EXISTS school_council_members (
                id TEXT PRIMARY KEY,
                school_id TEXT NOT NULL REFERENCES schools(id) ON DELETE RESTRICT,
                council_type TEXT NOT NULL CHECK(council_type IN ('مجلس الآباء','مجلس الأمهات')),
                full_name TEXT NOT NULL,
                position TEXT NOT NULL DEFAULT '',
                phone TEXT NOT NULL DEFAULT '',
                notes TEXT NOT NULL DEFAULT '',
                is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_school_councils_active
            ON school_council_members(school_id,council_type,is_active);
            """);
        cmd.ExecuteNonQuery();
    }

    private static bool CanEditSchoolCouncil(UserAccount actor, string schoolId) =>
        AccessPolicy.CanSeeSchool(actor, schoolId) &&
        (AccessPolicy.CanEditSchool(actor) ||
         AccessPolicy.CanEditSchoolProfile(actor, schoolId));

    private static void ValidateCouncil(ParentCouncilMember item)
    {
        if (item.CouncilType is not ("مجلس الآباء" or "مجلس الأمهات") ||
            string.IsNullOrWhiteSpace(item.FullName) || item.FullName.Length > 180 ||
            item.Position.Length > 120 || item.Phone.Length > 40 || item.Notes.Length > 1000 ||
            item.Phone.Any(c => !(char.IsAsciiDigit(c) || c is '+' or '-' or ' ')))
            throw new ArgumentException("راجع نوع المجلس واسم العضو ورقم هاتفه وملاحظاته.");
    }

    public IReadOnlyList<ParentCouncilMember> GetParentCouncilMembers(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeSchool(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض مجلس هذه المدرسة.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT id,school_id,council_type,full_name,position,phone,notes,is_active
            FROM school_council_members WHERE school_id=$s
            ORDER BY is_active DESC,council_type,full_name,id
            """, ("$s", schoolId));
        using var r = cmd.ExecuteReader();
        var list = new List<ParentCouncilMember>();
        while (r.Read()) list.Add(new ParentCouncilMember(
            Text(r, 0), Text(r, 1), Text(r, 2), Text(r, 3),
            Text(r, 4), Text(r, 5), Text(r, 6), r.GetInt32(7) == 1));
        return list;
    }

    public ParentCouncilMember SaveParentCouncilMember(UserAccount actor, string schoolId,
        ParentCouncilMember item)
    {
        RequireFreshActor(actor);
        if (!CanEditSchoolCouncil(actor, schoolId) || item.SchoolId != schoolId)
            throw new UnauthorizedAccessException("ليس لديك صلاحية تعديل مجلس هذه المدرسة.");
        ValidateCouncil(item);
        using var con = Open();
        using var tx = con.BeginTransaction();
        var id = string.IsNullOrWhiteSpace(item.Id) ? Id() : item.Id;
        var created = string.IsNullOrWhiteSpace(item.Id);
        if (created)
        {
            using var add = Cmd(con, tx, """
                INSERT INTO school_council_members(id,school_id,council_type,full_name,
                    position,phone,notes,is_active,created_utc,updated_utc)
                SELECT $id,s.id,$type,$name,$position,$phone,$notes,$active,$at,$at
                FROM schools s WHERE s.id=$school AND s.is_active=1
                """, ("$id", id), ("$school", schoolId), ("$type", item.CouncilType),
                ("$name", item.FullName.Trim()), ("$position", item.Position.Trim()),
                ("$phone", item.Phone.Trim()), ("$notes", item.Notes.Trim()),
                ("$active", item.IsActive ? 1 : 0), ("$at", Utc()));
            if (add.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("المدرسة غير نشطة أو غير موجودة.");
        }
        else
        {
            using var update = Cmd(con, tx, """
                UPDATE school_council_members
                SET council_type=$type,full_name=$name,position=$position,phone=$phone,
                    notes=$notes,is_active=$active,updated_utc=$at
                WHERE id=$id AND school_id=$school
                """, ("$id", id), ("$school", schoolId), ("$type", item.CouncilType),
                ("$name", item.FullName.Trim()), ("$position", item.Position.Trim()),
                ("$phone", item.Phone.Trim()), ("$notes", item.Notes.Trim()),
                ("$active", item.IsActive ? 1 : 0), ("$at", Utc()));
            if (update.ExecuteNonQuery() != 1)
                throw new UnauthorizedAccessException("عضو المجلس غير موجود في المدرسة المحددة.");
        }
        Audit(con, tx, actor.Id, created ? "council.create" : "council.update",
            id, $"school={schoolId};type={item.CouncilType}");
        tx.Commit();
        return item with { Id = id, FullName = item.FullName.Trim(),
            Phone = item.Phone.Trim(), Position = item.Position.Trim(), Notes = item.Notes.Trim() };
    }

    /// <summary>Archive council members instead of destroying school history; reversible.</summary>
    public void SetParentCouncilMemberActive(UserAccount actor, string schoolId, string id, bool active)
    {
        RequireFreshActor(actor);
        if (!CanEditSchoolCouncil(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية تعديل أعضاء هذا المجلس.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = Cmd(con, tx, """
            UPDATE school_council_members
            SET is_active=$active,updated_utc=$at
            WHERE id=$id AND school_id=$school
            """, ("$id", id), ("$school", schoolId),
            ("$active", active ? 1 : 0), ("$at", Utc()));
        if (cmd.ExecuteNonQuery() != 1)
            throw new UnauthorizedAccessException("عضو المجلس غير موجود في المدرسة.");
        Audit(con, tx, actor.Id, active ? "council.restore" : "council.archive",
            id, $"school={schoolId}");
        tx.Commit();
    }

    /// <summary>
    /// Real-time operational counts derived from the same tables used in the school,
    /// staff and student pages. Official approved census totals stay separate.
    /// </summary>
    public LiveDirectoryTotals GetLiveDirectoryTotals(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض بيانات المدارس.");
        var canViewStudents = AccessPolicy.Has(actor, PermissionFlags.ViewStudents);
        var canViewStaff = AccessPolicy.Has(actor, PermissionFlags.ViewStaff);
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT
              (SELECT COUNT(*) FROM schools s WHERE s.is_active=1
                  AND ($scope IS NULL OR s.id=$scope)),
              (SELECT COUNT(*) FROM school_students st
               JOIN student_enrollments e ON e.student_id=st.id AND e.school_id=st.school_id
               JOIN schools s ON s.id=st.school_id
               WHERE st.is_active=1 AND s.is_active=1 AND e.academic_year=$year
                 AND ($scope IS NULL OR s.id=$scope)),
              (SELECT COUNT(*) FROM school_staff st JOIN schools s ON s.id=st.school_id
               WHERE st.is_active=1 AND st.category='معلم' AND s.is_active=1
                 AND ($scope IS NULL OR s.id=$scope)),
              (SELECT COUNT(*) FROM school_staff st JOIN schools s ON s.id=st.school_id
               WHERE st.is_active=1 AND st.category='إداري' AND s.is_active=1
                 AND ($scope IS NULL OR s.id=$scope)),
              (SELECT COUNT(*) FROM school_staff st JOIN schools s ON s.id=st.school_id
               WHERE st.is_active=1 AND st.category='أخصائي اجتماعي' AND s.is_active=1
                 AND ($scope IS NULL OR s.id=$scope)),
              (SELECT COUNT(*) FROM school_grades g JOIN schools s ON s.id=g.school_id
               WHERE g.academic_year=$year AND s.is_active=1
                 AND ($scope IS NULL OR s.id=$scope)),
              (SELECT COUNT(*) FROM school_council_members c JOIN schools s ON s.id=c.school_id
               WHERE c.is_active=1 AND s.is_active=1
                 AND ($scope IS NULL OR s.id=$scope)),
              (SELECT COUNT(*) FROM school_staff st JOIN schools s ON s.id=st.school_id
                JOIN staff_personnel_details p ON p.staff_id=st.id
                WHERE st.is_active=1 AND s.is_active=1 AND p.assignment_status='مباشر'
                  AND ($scope IS NULL OR s.id=$scope)),
              (SELECT COUNT(*) FROM school_staff st JOIN schools s ON s.id=st.school_id
                JOIN staff_personnel_details p ON p.staff_id=st.id
                WHERE st.is_active=1 AND s.is_active=1 AND p.assignment_status IN ('منتدب','مكلف','معار')
                  AND ($scope IS NULL OR s.id=$scope)),
              (SELECT COUNT(*) FROM school_staff st JOIN schools s ON s.id=st.school_id
                LEFT JOIN staff_personnel_details p ON p.staff_id=st.id
                WHERE st.is_active=1 AND s.is_active=1
                  AND COALESCE(p.assignment_status,'غير محدد') NOT IN ('مباشر','منتدب','مكلف','معار')
                  AND ($scope IS NULL OR s.id=$scope)),
              (SELECT CASE WHEN $scope IS NOT NULL THEN 0 ELSE COUNT(*) END
               FROM (
                  SELECT TRIM(employee_code) AS code FROM school_staff
                  WHERE LENGTH(TRIM(employee_code))>0
                  GROUP BY TRIM(employee_code) COLLATE NOCASE
                  HAVING COUNT(DISTINCT school_id)>1
               ))
            """, ("$scope", actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId : null),
            ("$year", AcademicYear));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("تعذر حساب المؤشرات المباشرة.");
        int Number(int column) => checked((int)r.GetInt64(column));
        return new LiveDirectoryTotals(Number(0),
            canViewStudents ? Number(1) : 0,
            canViewStaff ? Number(2) : 0,
            canViewStaff ? Number(3) : 0,
            canViewStaff ? Number(4) : 0,
            canViewStudents ? Number(5) : 0,
            Number(6), canViewStudents, canViewStaff,
            canViewStaff ? Number(7) : 0,
            canViewStaff ? Number(8) : 0,
            canViewStaff ? Number(9) : 0,
            canViewStaff && actor.Role is not (Roles.Entry or Roles.Principal) ? Number(10) : 0);
    }
}
