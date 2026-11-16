using System.Globalization;

namespace SchoolObservatory.Core;

/// <summary>Import row for a single school; school code may be empty when the selected school is authoritative.</summary>
public sealed record StudentImportRow(string SchoolCode, string StudentCode, string FullName,
    int Grade, string Section, string Gender, string GuardianName, string GuardianPhone);

/// <summary>
/// Imports literal rows only from protected, bounded .xlsx/.docx structured tables.
/// The chosen school is still verified by the database for every row.
/// </summary>
public static class StudentImportParser
{
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["school_code"] = "school_code", ["رمز المدرسة"] = "school_code",
        ["student_code"] = "student_code", ["رمز الطالب"] = "student_code",
        ["رقم الطالب"] = "student_code", ["الرقم المدرسي"] = "student_code",
        ["full_name"] = "full_name", ["الاسم"] = "full_name",
        ["اسم الطالب"] = "full_name", ["اسم الطالبة"] = "full_name",
        ["الاسم الكامل"] = "full_name",
        ["grade"] = "grade", ["الصف"] = "grade", ["الصف الدراسي"] = "grade",
        ["section"] = "section", ["الشعبة"] = "section", ["الفصل"] = "section",
        ["gender"] = "gender", ["الجنس"] = "gender", ["نوع الطالب"] = "gender",
        ["guardian_name"] = "guardian_name", ["ولي الأمر"] = "guardian_name",
        ["اسم ولي الأمر"] = "guardian_name",
        ["guardian_phone"] = "guardian_phone", ["هاتف ولي الأمر"] = "guardian_phone",
        ["رقم هاتف ولي الأمر"] = "guardian_phone"
    };

    private static Dictionary<string, int> Map(IReadOnlyList<string> columns)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            if (!Headers.TryGetValue(columns[i].Trim(), out var key)) continue;
            if (!map.TryAdd(key, i))
                throw new ArgumentException("يحتوي الجدول عناوين مكررة للحقل نفسه.");
        }
        return map;
    }

    private static bool HasHeaders(IReadOnlyList<string> columns)
    {
        var known = columns.Select(x => Headers.GetValueOrDefault(x.Trim(), "")).ToHashSet();
        return new[] { "student_code", "full_name", "grade" }.All(known.Contains);
    }

    public static IReadOnlyList<StudentImportRow> Parse(string path)
    {
        var rows = SchoolStaffImportParser.ReadStructuredTable(path, HasHeaders);
        if (rows.Count < 2 || !HasHeaders(rows[0]))
            throw new ArgumentException("الجدول يجب أن يحتوي رقم الطالب والاسم والصف الدراسي.");
        var indices = Map(rows[0]);
        string Value(IReadOnlyList<string> row, string key) =>
            indices.TryGetValue(key, out var i) && i < row.Count ? row[i].Trim() : "";

        var result = new List<StudentImportRow>();
        foreach (var row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var gradeValue = Value(row, "grade");
            if (!int.TryParse(gradeValue, NumberStyles.None, CultureInfo.InvariantCulture, out var grade) ||
                grade is < 1 or > 12)
                throw new ArgumentException("الصف الدراسي يجب أن يكون رقما صحيحا من 1 إلى 12.");
            var section = Value(row, "section");
            result.Add(new StudentImportRow(Value(row, "school_code"),
                Value(row, "student_code"), Value(row, "full_name"), grade,
                string.IsNullOrWhiteSpace(section) ? "أ" : section,
                Value(row, "gender"), Value(row, "guardian_name"), Value(row, "guardian_phone")));
            if (result.Count > 5000) throw new ArgumentException("الملف يتجاوز 5000 طالب.");
        }
        if (result.Count == 0) throw new ArgumentException("ملف الاستيراد لا يحتوي سجلات طلاب.");
        return result;
    }
}
