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
                   CASE WHEN a.id IS NULL THEN 'غير محدد'
                        WHEN a.start_date IS NULL AND
                             a.notes IN ('سجل أصلي','سجل أصلي مرحل')
                             AND COALESCE(p.assignment_status,'غير محدد')<>'غير محدد'
                        THEN p.assignment_status
                        ELSE a.assignment_type END,
                   CASE WHEN a.id IS NULL THEN 'غير محدد'
                        WHEN a.start_date IS NULL AND
                             a.notes IN ('سجل أصلي','سجل أصلي مرحل')
                             AND p.assignment_status IN ('منتدب','مكلف','معار')
                             AND NULLIF(TRIM(p.actual_workplace),'') IS NOT NULL
                        THEN TRIM(p.actual_workplace)
                        ELSE COALESCE(work.name,'غير محدد') END,
                   st.is_active
            FROM school_staff st
            JOIN schools sc ON sc.id=st.school_id
            LEFT JOIN staff_personnel_details p ON p.staff_id=st.id
            LEFT JOIN employee_assignments a ON a.id=(
                SELECT ea.id FROM employee_assignments ea
                WHERE ea.employee_id=st.id
                    AND (ea.start_date IS NULL OR ea.start_date<=$today)
                    AND (ea.end_date IS NULL OR ea.end_date>=$today)
                ORDER BY ea.start_date DESC,ea.created_utc DESC LIMIT 1)
            LEFT JOIN schools work ON work.id=a.workplace_school_id
            WHERE ($scope IS NULL OR st.school_id=$scope)
            ORDER BY sc.name,st.category,st.full_name,st.employee_code
            """, ("$scope", actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId : null),
            ("$today", DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
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
