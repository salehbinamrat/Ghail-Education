namespace SchoolObservatory.Core;

/// <summary>Local class-roster search. Never sends a search keystroke to SQLite.</summary>
public static class SchoolStudentSearch
{
    public static IReadOnlyList<SchoolStudent> Find(IReadOnlyList<SchoolStudent> students, string? query)
    {
        ArgumentNullException.ThrowIfNull(students);
        if (string.IsNullOrWhiteSpace(query))
            return students;

        var term = query.Trim();
        var matches = new List<SchoolStudent>();
        foreach (var student in students)
        {
            if (student.StudentCode.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                student.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                student.GuardianName.Contains(term, StringComparison.OrdinalIgnoreCase))
                matches.Add(student);
        }
        return matches;
    }
}
