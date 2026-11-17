using System.Globalization;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>Read-only, pre-authorized rows. This partial contains no migration or mutation.</summary>
public sealed partial class SqliteStore
{
    public IReadOnlyList<SchoolGrade> GetReportSchoolGrades(UserAccount actor,
        string schoolId, string academicYear)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException("عرض الشعب غير مسموح لهذه المدرسة.");
        using var con = Open();
        using var command = Cmd(con, null, """
            SELECT g.id,g.school_id,g.academic_year,g.grade,g.section,
              COALESCE(NULLIF(g.academic_stage,''),CASE WHEN s.is_kindergarten=1 THEN 'روضة'
                   WHEN s.stage='أساسي وثانوي' THEN 'مجمع تربوي' ELSE s.stage END)
            FROM school_grades g JOIN schools s ON s.id=g.school_id
            WHERE g.school_id=$s AND g.academic_year=$y
            ORDER BY g.grade,g.section
            """, ("$s", schoolId), ("$y", academicYear));
        using var reader = command.ExecuteReader();
        var result = new List<SchoolGrade>();
        while (reader.Read())
            result.Add(new SchoolGrade(Text(reader, 0), Text(reader, 1),
                Text(reader, 2), reader.GetInt32(3), Text(reader, 4), Text(reader, 5)));
        return result;
    }

    public sealed record ReportStudentResult(string StudentId, string Term,
        string Subject, int Score, int Maximum);

    public IReadOnlyList<ReportStudentResult> GetReportStudentResults(UserAccount actor,
        string schoolId, string academicYear)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException("نتائج هذه المدرسة غير متاحة.");
        using var con = Open();
        using var command = Cmd(con, null, """
            SELECT student_id,term,subject,score,maximum FROM student_results
                WHERE school_id=$s AND academic_year=$y
            UNION ALL
            SELECT student_id,term,subject,score,maximum FROM transferred_student_results
                WHERE school_id=$s AND academic_year=$y
            ORDER BY student_id,term,subject
            """, ("$s", schoolId), ("$y", academicYear));
        using var reader = command.ExecuteReader();
        var result = new List<ReportStudentResult>();
        while (reader.Read())
            result.Add(new ReportStudentResult(Text(reader, 0), Text(reader, 1),
                Text(reader, 2), reader.GetInt32(3), reader.GetInt32(4)));
        return result;
    }

    public sealed record ReportAttendanceRecord(string StudentId, string Date,
        string Status);

    public IReadOnlyList<ReportAttendanceRecord> GetReportStudentAttendance(UserAccount actor,
        string schoolId, string academicYear)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException("حضور هذه المدرسة غير متاح.");
        using var con = Open();
        // Older attendance with NULL year is deliberately excluded, never guessed by calendar date.
        using var command = Cmd(con, null, """
            SELECT student_id,attendance_date,status FROM student_attendance
                WHERE school_id=$s AND academic_year=$y
            UNION ALL
            SELECT student_id,attendance_date,status FROM transferred_student_attendance
                WHERE school_id=$s AND academic_year=$y
            ORDER BY attendance_date,student_id
            """, ("$s", schoolId), ("$y", academicYear));
        using var reader = command.ExecuteReader();
        var result = new List<ReportAttendanceRecord>();
        while (reader.Read())
            result.Add(new ReportAttendanceRecord(Text(reader, 0), Text(reader, 1), Text(reader, 2)));
        return result;
    }

    /// <summary>Historic approved snapshots for the requested round, not the currently active round.</summary>
    public IReadOnlyList<CensusSubmission> GetReportCensusSubmissions(
        UserAccount actor, string academicYear)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewReports))
            throw new UnauthorizedAccessException("لا يمكن إصدار تقارير الإحصاء.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            WITH most_recent AS (
                SELECT s.school_id, MAX(s.revision) AS revision
                FROM submissions s
                JOIN census_rounds cr ON cr.id=s.round_id
                WHERE cr.academic_year=$year
                GROUP BY s.school_id
            )
            SELECT s.id,s.school_id,school.name,round.academic_year,s.revision,s.status,
                   s.male_teachers,s.female_teachers,s.classrooms,s.notes,s.decision_reason,s.created_utc,
                   e.grade,e.boys,e.girls
            FROM most_recent recent
            JOIN submissions s ON s.school_id=recent.school_id
                 AND s.revision=recent.revision
            JOIN census_rounds round ON round.id=s.round_id AND round.academic_year=$year
            JOIN schools school ON school.id=s.school_id
            LEFT JOIN enrollment_rows e ON e.submission_id=s.id
            WHERE ($scope IS NULL OR s.school_id=$scope)
            ORDER BY s.school_id,CAST(e.grade AS INTEGER)
            """, ("$year", academicYear),
               ("$scope", actor.Role is Roles.Entry or Roles.Principal
                    ? actor.SchoolId ?? "" : null));
        using var reader = cmd.ExecuteReader();
        var result = new List<CensusSubmission>();
        string? lastId = null;
        List<EnrollmentLine>? grades = null;
        while (reader.Read())
        {
            var id = Text(reader, 0);
            if (id != lastId)
            {
                grades = new List<EnrollmentLine>();
                result.Add(new CensusSubmission(
                    id, Text(reader, 1), Text(reader, 2), Text(reader, 3),
                    reader.GetInt32(4), Text(reader, 5),
                    new CensusData(grades, reader.GetInt32(6), reader.GetInt32(7),
                        reader.GetInt32(8), Text(reader, 9)),
                    NullableText(reader, 10),
                    DateTimeOffset.Parse(Text(reader, 11), CultureInfo.InvariantCulture)));
                lastId = id;
            }
            if (!reader.IsDBNull(12))
                grades!.Add(new EnrollmentLine(Text(reader, 12), reader.GetInt32(13),
                    reader.GetInt32(14)));
        }
        return result;
    }
}
