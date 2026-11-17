using System.Text;

namespace SchoolObservatory.Core;

/// <summary>Local class-roster search. Never sends a search keystroke to SQLite.</summary>
public static class SchoolStudentSearch
{
    public static IReadOnlyList<SchoolStudent> Find(IReadOnlyList<SchoolStudent> students, string? query)
    {
        ArgumentNullException.ThrowIfNull(students);
        if (string.IsNullOrWhiteSpace(query))
            return students;

        var original = query.Trim();
        var term = Normalize(original);
        if (term.Length == 0)
            return students;

        // Codes and phone numbers are stored in ASCII. Preserve the fast path for
        // Latin/numeric searches; Arabic queries allow spelling and numeral variants.
        var arabicQuery = original.Any(c => c > 127);
        bool Matches(string value) =>
            value.Contains(original, StringComparison.OrdinalIgnoreCase) ||
            (arabicQuery && Normalize(value).Contains(term, StringComparison.OrdinalIgnoreCase));

        var matches = new List<SchoolStudent>();
        foreach (var student in students)
        {
            if (Matches(student.StudentCode) || Matches(student.FullName) ||
                Matches(student.GuardianName) || Matches(student.GuardianPhone))
                matches.Add(student);
        }
        return matches;
    }

    private static string Normalize(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var value in input)
        {
            if (value is '\u0640' or '\u0670' ||
                value is >= '\u064B' and <= '\u065F' ||
                value is >= '\u06D6' and <= '\u06ED')
                continue;

            builder.Append(value switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                >= '٠' and <= '٩' => (char)('0' + value - '٠'),
                >= '۰' and <= '۹' => (char)('0' + value - '۰'),
                _ => value
            });
        }
        return builder.ToString();
    }
}
