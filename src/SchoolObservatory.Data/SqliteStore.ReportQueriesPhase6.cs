using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed record ReportSectionCount(int Grade, string Section, int Students);
public sealed record CensusStatusReportRow(
    string SchoolId, string SchoolName, string AcademicYear, int Revision,
    string Status, int Boys, int Girls, int Students, int Teachers, int Classrooms);

public sealed partial class SqliteStore
{
    /// <summary>Includes registered grade/sections with zero students, using the same
    /// enrolled student identity and school/year status as operational snapshots.</summary>
    public IReadOnlyList<ReportSectionCount> GetSectionCountsForReport(
        UserAccount actor, string schoolId, string year, int? grade = null,
        string? section = null, string? gender = null)
    {
        RequireFreshActor(actor);
        if (!ReportAccess.CanRead(actor, schoolId, PermissionFlags.ViewStudents))
            throw new UnauthorizedAccessException("ليست لديك صلاحية تصدير صفوف هذه المدرسة.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT g.grade,g.section,
                   COUNT(DISTINCT CASE
                     WHEN st.id IS NOT NULL
                       AND ($historic=1 OR st.is_active=1)
                       AND ($gender IS NULL OR st.gender=$gender)
                     THEN h.student_id END)
            FROM school_grades g
            LEFT JOIN student_history h ON h.grade_id=g.id
                AND h.school_id=g.school_id
                AND h.academic_year=g.academic_year
                AND h.status='مسجل' AND h.end_utc IS NULL
            LEFT JOIN school_students st ON st.id=h.student_id
            WHERE g.school_id=$school AND g.academic_year=$year
              AND ($grade IS NULL OR g.grade=$grade)
              AND ($section IS NULL OR g.section=$section)
            GROUP BY g.id,g.grade,g.section
            ORDER BY g.grade,g.section
            """,
            ("$school",schoolId), ("$year",year),
            ("$historic",year == AcademicYear ? 0 : 1),
            ("$grade",grade), ("$section",section), ("$gender",gender));
        using var r = cmd.ExecuteReader();
        var result = new List<ReportSectionCount>();
        while (r.Read())
            result.Add(new ReportSectionCount(r.GetInt32(0),Text(r,1),Count64(r,2)));
        return result;
    }

    /// <summary>Latest submission for each school in a selected year, regardless of
    /// current active round. Status reports are never themselves approved documents.</summary>
    public IReadOnlyList<CensusStatusReportRow> GetCensusStatusesForReport(
        UserAccount actor, string year, string? status = null)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewReports) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException("ليست لديك صلاحية تقارير الإحصاء.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            WITH latest AS (
              SELECT s.id,s.school_id,r.academic_year,s.revision,s.status,
                     s.male_teachers+s.female_teachers AS teachers,s.classrooms,
                     ROW_NUMBER() OVER (PARTITION BY s.school_id
                       ORDER BY r.snapshot_date DESC,s.revision DESC,s.created_utc DESC) rn
              FROM submissions s
              JOIN census_rounds r ON r.id=s.round_id
              WHERE r.academic_year=$year
                AND ($scope IS NULL OR s.school_id=$scope)
            )
            SELECT a.school_id,sc.name,a.academic_year,a.revision,a.status,
                   COALESCE(SUM(e.boys),0),COALESCE(SUM(e.girls),0),
                   COALESCE(SUM(e.boys+e.girls),0),a.teachers,a.classrooms
            FROM latest a
            JOIN schools sc ON sc.id=a.school_id
            LEFT JOIN enrollment_rows e ON e.submission_id=a.id
            WHERE a.rn=1 AND ($status IS NULL OR a.status=$status)
            GROUP BY a.school_id,sc.name,a.academic_year,a.revision,
                     a.status,a.teachers,a.classrooms
            ORDER BY sc.zone,sc.name
            """, ("$year",year), ("$status",status),
            ("$scope",actor.Role is Roles.Entry or Roles.Principal
                ? actor.SchoolId ?? "" : null));
        using var r = cmd.ExecuteReader();
        var rows = new List<CensusStatusReportRow>();
        while (r.Read())
            rows.Add(new CensusStatusReportRow(Text(r,0),Text(r,1),Text(r,2),
                r.GetInt32(3),Text(r,4),Count64(r,5),Count64(r,6),Count64(r,7),
                Count64(r,8),Count64(r,9)));
        return rows;
    }
}
