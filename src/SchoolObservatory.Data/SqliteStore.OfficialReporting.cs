using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed record ApprovedCensusReportRow(
    string SchoolId, string SchoolName, string AcademicYear,
    int Boys, int Girls, int Students, int Teachers, int Classrooms, string SubmissionId);

public sealed partial class SqliteStore
{
    /// <summary>
    /// Read the most recent APPROVED snapshot per school/year. This deliberately does
    /// not use GetLatestSubmissions (whose latest revision may be a later draft).
    /// The final approved source is immutable under Phase 3 DB triggers.
    /// </summary>
    public IReadOnlyList<ApprovedCensusReportRow> GetApprovedCensusReport(
        UserAccount actor, string academicYear)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewReports) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException("لا تملك صلاحية تصدير الإحصاء المعتمد.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            WITH approved AS (
                SELECT s.id,s.school_id,r.academic_year,
                       s.male_teachers+s.female_teachers AS teachers,
                       s.classrooms,
                       ROW_NUMBER() OVER (
                         PARTITION BY s.school_id
                         ORDER BY r.snapshot_date DESC,s.revision DESC,s.created_utc DESC
                       ) rn
                FROM submissions s
                JOIN census_rounds r ON r.id=s.round_id
                WHERE s.status='approved' AND r.academic_year=$year
                  AND ($scope IS NULL OR s.school_id=$scope)
            )
            SELECT a.school_id,sc.name,a.academic_year,
                   COALESCE(SUM(e.boys),0),COALESCE(SUM(e.girls),0),
                   COALESCE(SUM(e.boys+e.girls),0),a.teachers,a.classrooms,a.id
            FROM approved a
            JOIN schools sc ON sc.id=a.school_id
            LEFT JOIN enrollment_rows e ON e.submission_id=a.id
            WHERE a.rn=1
            GROUP BY a.school_id,sc.name,a.academic_year,a.teachers,a.classrooms,a.id
            ORDER BY sc.zone,sc.name
            """, ("$year", academicYear),
            ("$scope", actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId ?? "" : null));
        var rows = new List<ApprovedCensusReportRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new ApprovedCensusReportRow(Text(r,0), Text(r,1), Text(r,2),
                r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),
                r.GetInt32(6),r.GetInt32(7),Text(r,8)));
        return rows;
    }

    /// <summary>Complete, academic-year-qualified attendance rows; no hidden 1000-row cap.</summary>
    public IReadOnlyList<StudentAttendance> GetAttendanceForReport(
        UserAccount actor, string schoolId, string academicYear, string studentId)
    {
        RequireFreshActor(actor);
        if (!ReportAccess.CanRead(actor, schoolId, PermissionFlags.ViewStudents))
            throw new UnauthorizedAccessException("لا تملك صلاحية كشف حضور هذه المدرسة.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT id,student_id,attendance_date,status,notes
            FROM student_attendance
            WHERE school_id=$school AND student_id=$student AND academic_year=$year
            UNION ALL
            SELECT id,student_id,attendance_date,status,notes
            FROM transferred_student_attendance
            WHERE school_id=$school AND student_id=$student AND academic_year=$year
            ORDER BY attendance_date DESC
            """, ("$school", schoolId), ("$student", studentId), ("$year", academicYear));
        using var r = cmd.ExecuteReader();
        var result = new List<StudentAttendance>();
        while (r.Read())
            result.Add(new StudentAttendance(Text(r,0),Text(r,1),Text(r,2),Text(r,3),Text(r,4)));
        return result;
    }
}
