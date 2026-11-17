using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    /// <summary>
    /// Read-only historical roster. It includes archived/graduated/transferred placements,
    /// but never crosses the authenticated user's school boundary. Unlike the live roster,
    /// it does not use the currently active academic year and does not mutate closed records.
    /// </summary>
    public IReadOnlyList<SchoolStudent> GetHistoricalStudents(UserAccount actor,
        string schoolId, string academicYear)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض كشوف هذه المدرسة.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            WITH ranked AS (
                SELECT st.id,h.school_id,st.student_code,st.full_name,
                    g.grade,g.section,st.gender,st.guardian_name,st.guardian_phone,
                    st.notes,st.is_active,h.academic_year,
                    ROW_NUMBER() OVER (
                        PARTITION BY h.student_id
                        ORDER BY h.start_utc DESC,h.id DESC) AS rn
                FROM student_history h
                JOIN school_students st ON st.id=h.student_id
                JOIN school_grades g ON g.id=h.grade_id
                WHERE h.school_id=$s AND h.academic_year=$year
            )
            SELECT id,school_id,student_code,full_name,grade,section,
                gender,guardian_name,guardian_phone,notes,is_active,academic_year
            FROM ranked WHERE rn=1
            ORDER BY grade,section,full_name,student_code
            """, ("$s", schoolId), ("$year", academicYear));
        using var r = cmd.ExecuteReader();
        var rows = new List<SchoolStudent>();
        while (r.Read()) rows.Add(ReadStudent(r));
        return rows;
    }
}
