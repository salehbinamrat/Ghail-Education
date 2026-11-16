using System.Text;

namespace SchoolObservatory.Core;

public sealed record SchoolImport(string Code, string Name, string Zone, string Ownership, string Stage,
    string Gender = "مختلطة");

/// <summary>RFC-4180-like CSV reader for school-directory imports. Entire file is validated before writing.</summary>
public static class SchoolCsvParser
{
    private static readonly string[] RequiredHeaders = ["code", "name", "zone", "ownership", "stage"];

    /// <summary>Reject undecodable UTF-8 instead of silently saving corrupted Arabic school names.</summary>
    public static IReadOnlyList<SchoolImport> ParseUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 5_000_000)
            throw new ArgumentException("حجم الملف أكبر من الحد المسموح (5 ميجابايت).");
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(bytes);
        return Parse(text);
    }


    public static IReadOnlyList<SchoolImport> Parse(string csv)
    {
        if (csv.Length > 5_000_000) throw new ArgumentException("حجم الملف أكبر من الحد المسموح (5 ميجابايت).");
        var rows = ParseRows(csv.TrimStart('\uFEFF'));
        if (rows.Count == 0) throw new ArgumentException("الملف فارغ.");
        var header = rows[0].Select(s => s.Trim().ToLowerInvariant()).ToArray();
        if ((header.Length is not (5 or 6)) || !header.Take(5).SequenceEqual(RequiredHeaders) ||
            (header.Length == 6 && header[5] != "gender"))
            throw new ArgumentException("الحقول: code,name,zone,ownership,stage ثم gender اختياريا.");
        var imports = new List<SchoolImport>();
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in rows.Skip(1))
        {
            if (line.Count == 1 && line[0].Length == 0) continue;
            if (line.Count != header.Length) throw new ArgumentException("عدد الأعمدة غير صحيح في صف الاستيراد.");
            var import = new SchoolImport(line[0].Trim(), line[1].Trim(), line[2].Trim(), line[3].Trim(), line[4].Trim(),
                header.Length == 6 ? line[5].Trim() : "مختلطة");
            if (import.Code.Length == 0 || import.Name.Length == 0 || import.Zone.Length == 0 ||
                import.Stage.Length == 0 || import.Ownership is not ("حكومية" or "أهلية") ||
                import.Gender is not ("ذكور" or "إناث" or "مختلطة"))
                throw new ArgumentException($"توجد قيم ناقصة أو غير صحيحة عند المدرسة {import.Code}.");
            if (!codes.Add(import.Code)) throw new ArgumentException($"رمز مدرسة مكرر داخل الملف: {import.Code}");
            imports.Add(import);
            if (imports.Count > 3_000) throw new ArgumentException("الحد الأقصى للمدارس في الملف الواحد 3000.");
        }
        if (imports.Count == 0) throw new ArgumentException("الملف لا يحتوي مدارس.");
        return imports;
    }

    private static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        bool afterQuote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') { quoted = false; afterQuote = true; }
                else cell.Append(c);
            }
            else if (afterQuote)
            {
                if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); afterQuote = false; }
                else if (c is '\n' or '\r')
                {
                    cells.Add(cell.ToString()); cell.Clear(); afterQuote = false;
                    rows.Add(cells); cells = new List<string>();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
                else if (c is ' ' or '\t') { /* allow outer spaces */ }
                else throw new ArgumentException("توجد أحرف غير صحيحة بعد علامة الاقتباس.");
            }
            else
            {
                if (c == '"')
                {
                    if (cell.Length != 0) throw new ArgumentException("اقتباس غير صحيح في ملف CSV.");
                    quoted = true;
                }
                else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
                else if (c is '\n' or '\r')
                {
                    cells.Add(cell.ToString()); cell.Clear();
                    rows.Add(cells); cells = new List<string>();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
                else cell.Append(c);
            }
        }
        if (quoted) throw new ArgumentException("علامة اقتباس لم تغلق في ملف CSV.");
        if (afterQuote || cells.Count > 0 || cell.Length > 0)
        {
            cells.Add(cell.ToString()); rows.Add(cells);
        }
        return rows;
    }
}
