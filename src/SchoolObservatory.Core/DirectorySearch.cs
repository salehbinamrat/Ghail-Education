using System.Text;

namespace SchoolObservatory.Core;

/// <summary>
/// Performs school and personnel directory filtering locally against the loaded rows.
/// No database query or network request runs when the user types into search fields.
/// </summary>
public static class DirectorySearch
{
    private static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            if (c is '\u0640' or '\u0670' or >= '\u064B' and <= '\u065F')
                continue;
            result.Append(c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                >= '٠' and <= '٩' => (char)('0' + c - '٠'),
                >= '۰' and <= '۹' => (char)('0' + c - '۰'),
                _ => c
            });
        }
        return result.ToString();
    }

    private static bool Matches(string value, string term) =>
        Normalize(value).Contains(term, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<School> FindSchools(IReadOnlyList<School> schools, string? query)
    {
        ArgumentNullException.ThrowIfNull(schools);
        if (string.IsNullOrWhiteSpace(query)) return schools;
        var term = Normalize(query);
        if (term.Length == 0) return schools;
        return schools.Where(s => Matches(s.Code, term) || Matches(s.Name, term) ||
            Matches(s.Zone, term) || Matches(s.Stage, term)).ToArray();
    }

    public static IReadOnlyList<SchoolStaff> FindStaff(IReadOnlyList<SchoolStaff> staff, string? query)
    {
        ArgumentNullException.ThrowIfNull(staff);
        if (string.IsNullOrWhiteSpace(query)) return staff;
        var term = Normalize(query);
        if (term.Length == 0) return staff;
        return staff.Where(s => Matches(s.EmployeeCode, term) || Matches(s.FullName, term) ||
            Matches(s.Category, term) || Matches(s.JobTitle, term) ||
            Matches(s.Specialization, term) || Matches(s.Qualification, term)).ToArray();
    }
    /// <summary>Filter the already-loaded unified district roster without repeated SQL queries.</summary>
    public static IReadOnlyList<DistrictStaffDirectoryRow> FindDistrictStaff(
        IReadOnlyList<DistrictStaffDirectoryRow> people, string? query)
    {
        ArgumentNullException.ThrowIfNull(people);
        if (string.IsNullOrWhiteSpace(query)) return people;
        var term = Normalize(query);
        if (term.Length == 0) return people;
        return people.Where(x =>
            Matches(x.FullName, term) || Matches(x.EmployeeCode, term) ||
            Matches(x.SchoolName, term) || Matches(x.Category, term) ||
            Matches(x.JobTitle, term) || Matches(x.Qualification, term) ||
            Matches(x.AssignmentStatus, term) ||
            Matches(x.EffectiveWorkplace, term)).ToArray();
    }

}
