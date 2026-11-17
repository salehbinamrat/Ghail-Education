using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>
/// Live personnel directory: school registration is the canonical identity;
/// actual assignment is a separate optional record. No duplicate copies are created.
/// </summary>
public sealed partial class SqliteStore
{
    public IReadOnlyList<DistrictStaffDirectoryRow> GetDistrictStaffDirectory(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewStaff))
            throw new UnauthorizedAccessException("ليس لديك صلاحية عرض دليل الكادر.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT st.id,st.school_id,sc.name,st.employee_code,st.full_name,
                   st.category,st.job_title,st.qualification,
                   COALESCE(p.employment_type,'غير محدد'),
                   COALESCE(p.assignment_status,'غير محدد'),
                   CASE
                     WHEN p.assignment_status IN ('منتدب','مكلف','معار')
                       THEN COALESCE(NULLIF(TRIM(p.actual_workplace),''),'جهة غير مسجلة')
                     WHEN p.assignment_status='مباشر'
                       THEN COALESCE(NULLIF(TRIM(p.actual_workplace),''),sc.name)
                     ELSE 'غير محدد'
                   END,
                   st.is_active
            FROM school_staff st
            JOIN schools sc ON sc.id=st.school_id
            LEFT JOIN staff_personnel_details p ON p.staff_id=st.id
            WHERE ($scope IS NULL OR st.school_id=$scope)
            ORDER BY sc.name,st.category,st.full_name,st.employee_code
            """, ("$scope", actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId : null));
        var result = new List<DistrictStaffDirectoryRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new DistrictStaffDirectoryRow(
                Text(r,0), Text(r,1), Text(r,2), Text(r,3),
                Text(r,4), Text(r,5), Text(r,6), Text(r,7),
                Text(r,8), Text(r,9), Text(r,10), r.GetInt32(11)==1));
        }
        return result;
    }

    /// <summary>Read-only non-destructive audit for conflicting legacy employee IDs.</summary>
    public IReadOnlyList<StaffCodeConflict> GetDistrictStaffCodeConflicts(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewStaff) ||
            actor.Role is Roles.Entry or Roles.Principal)
            throw new UnauthorizedAccessException("فحص أرقام الكادر على مستوى المديرية للمخولين فقط.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT MIN(TRIM(employee_code)), COUNT(DISTINCT school_id)
            FROM school_staff
            WHERE LENGTH(TRIM(employee_code))>0
            GROUP BY TRIM(employee_code) COLLATE NOCASE
            HAVING COUNT(DISTINCT school_id)>1
            ORDER BY MIN(TRIM(employee_code))
            """);
        using var r = cmd.ExecuteReader();
        var result = new List<StaffCodeConflict>();
        while (r.Read())
            result.Add(new StaffCodeConflict(Text(r,0),r.GetInt32(1)));
        return result;
    }

    private static void EnsureUniqueDistrictEmployeeCode(SqliteConnection con,
        SqliteTransaction tx, string employeeCode, string schoolId, string staffId)
    {
        using var check = Cmd(con, tx, """
            SELECT EXISTS(
                SELECT 1 FROM school_staff
                WHERE employee_code=$code COLLATE NOCASE
                  AND school_id<>$school AND id<>$id)
            """, ("$code", employeeCode.Trim()), ("$school", schoolId), ("$id", staffId));
        if (Convert.ToInt32(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new ArgumentException(
                "الرقم الوظيفي مرتبط بالفعل بمدرسة أخرى. افتح السجل الأصلي وسجل الانتداب أو التكليف بدلا من تكرار الموظف.");
    }
}
