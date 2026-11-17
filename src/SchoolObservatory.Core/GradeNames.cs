using System.Globalization;
using System.Text;

namespace SchoolObservatory.Core;

/// <summary>
/// Arabic grade captions without changing the stable numeric grade keys in SQLite.
/// Kindergarten uses grades 1/2/3 for first/second/preparatory levels respectively,
/// scoped to schools whose stage is explicitly "روضة".
/// </summary>
public static class GradeNames
{
    private static readonly string[] Primary =
    [
        "أولى", "ثانية", "ثالثة", "رابعة", "خامسة", "سادسة", "سابعة", "ثامنة", "تاسعة"
    ];
    private static readonly string[] Secondary = ["أولى ثانوي", "ثانية ثانوي", "ثالثة ثانوي"];
    private static readonly string[] Kindergarten = ["روضة أولى", "روضة ثانية", "تمهيدي"];

    public static IEnumerable<int> Allowed(string stage) => stage switch
    {
        "روضة" => Enumerable.Range(1, 3),
        "أساسي" => Enumerable.Range(1, 9),
        "ثانوي" => Enumerable.Range(10, 3),
        _ => Enumerable.Range(1, 12)
    };

    public static bool IsAllowed(string stage, int grade) =>
        grade is >= 1 and <= 12 && Allowed(stage).Contains(grade);

    public static string ForStage(string stage, int grade)
    {
        if (stage == "روضة" && grade is >= 1 and <= 3)
            return Kindergarten[grade - 1];
        if (grade is >= 1 and <= 9)
            return Primary[grade - 1];
        if (grade is >= 10 and <= 12)
            return Secondary[grade - 10];
        return grade.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? text, out int grade)
    {
        grade = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var value = Normalize(text);
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
        {
            grade = numeric;
            return grade is >= 1 and <= 12;
        }
        for (var n = 1; n <= 12; n++)
        {
            foreach (var stage in new[] { "أساسي", "ثانوي", "روضة" })
            {
                if (!IsAllowed(stage, n)) continue;
                if (value == Normalize(ForStage(stage, n)) ||
                    (stage == "أساسي" && value == Normalize(ForStage(stage, n) + " أساسي")))
                {
                    grade = n;
                    return true;
                }
            }
        }

        string[] simple = ["اولى", "ثانية", "ثالثة", "رابعة", "خامسة",
            "سادسة", "سابعة", "ثامنة", "تاسعة"];
        for (var n = 1; n <= simple.Length; n++)
        {
            if (value == Normalize(simple[n - 1]) ||
                value == Normalize("الصف " + simple[n - 1]) ||
                value == Normalize("الصف " + simple[n - 1] + " أساسي"))
            {
                grade = n;
                return true;
            }
        }
        string[] masculine = ["الاول", "الثاني", "الثالث", "الرابع", "الخامس",
            "السادس", "السابع", "الثامن", "التاسع"];
        for (var n = 1; n <= masculine.Length; n++)
        {
            if (value == Normalize(masculine[n - 1] + " الاساسي") ||
                value == Normalize("الصف " + masculine[n - 1] + " الاساسي"))
            {
                grade = n;
                return true;
            }
        }
        return false;
    }

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
                >= '٠' and <= '٩' => (char)('0' + c - '٠'),
                >= '۰' and <= '۹' => (char)('0' + c - '۰'),
                _ => c
            });
        }
        return result.ToString();
    }
}
