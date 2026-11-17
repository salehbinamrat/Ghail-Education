using System.Globalization;
using System.Text;

namespace SchoolObservatory.Core;

/// <summary>
/// Human-friendly numeric input for Arabic-language census forms. Blank optional
/// counts mean zero; other invalid data is rejected rather than being guessed.
/// </summary>
public static class CensusNumberInput
{
    public static int ParseNonNegative(string? input, string field)
    {
        var raw = (input ?? "").Trim();
        if (raw.Length == 0) return 0;
        var normalized = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is >= '0' and <= '9')
                normalized.Append(ch);
            else if (ch is >= '٠' and <= '٩')
                normalized.Append((char)('0' + (ch - '٠')));
            else if (ch is >= '۰' and <= '۹')
                normalized.Append((char)('0' + (ch - '۰')));
            else
                throw new ArgumentException(
                    $"خانة «{field}» تقبل الأعداد الصحيحة غير السالبة فقط، بالأرقام العربية أو الإنجليزية.");
        }
        if (!int.TryParse(normalized.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"العدد في خانة «{field}» أكبر من الحد المسموح.");
        return value;
    }

    /// <summary>Each grade/section in the active academic year is one teaching group, not a physical room.</summary>
    public static int CountRegisteredSections(IEnumerable<SchoolGrade> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        return sections.Select(x => (x.SchoolId, x.AcademicYear, x.Grade, x.Section))
            .Distinct().Count();
    }

    /// <summary>
    /// Snapshot suggestion only: this never edits a census submission. The UI asks
    /// the user to review and explicitly save any suggested figures.
    /// </summary>
    public static IReadOnlyDictionary<int, (int Boys, int Girls)> SuggestGradeTotals(
        IEnumerable<SchoolStudent> students, string academicYear)
    {
        ArgumentNullException.ThrowIfNull(students);
        if (string.IsNullOrWhiteSpace(academicYear))
            throw new ArgumentException("العام الدراسي غير محدد.", nameof(academicYear));
        var rows = students.Where(x => x.IsActive && x.AcademicYear == academicYear)
            .DistinctBy(x => x.Id).ToList();
        if (rows.Any(x => x.Gender is not ("ذكور" or "إناث")))
            throw new ArgumentException("بعض كشوف الطلاب لا تحتوي على جنس محدد؛ راجعها قبل التعبئة.");
        return rows.GroupBy(x => x.Grade)
            .ToDictionary(g => g.Key, g => (Boys: g.Count(x => x.Gender == "ذكور"),
                Girls: g.Count(x => x.Gender == "إناث")));
    }
}
