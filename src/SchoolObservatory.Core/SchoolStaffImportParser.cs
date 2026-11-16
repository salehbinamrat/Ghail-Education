using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SchoolObservatory.Core;

/// <summary>One personnel record from a structured Excel or Word table.</summary>
public sealed record StaffImportRow(
    string SchoolCode, string EmployeeCode, string FullName, string Category,
    string JobTitle, string Specialization, string Qualification, string Gender);

/// <summary>
/// Parses a bounded OOXML .xlsx worksheet or .docx table. No macros, remote resources,
/// legacy .xls/.doc or unstructured Word prose are executed or interpreted.
/// </summary>
public static class SchoolStaffImportParser
{
    private const long MaxFileSize = 8_000_000;
    private const long MaxXmlBytes = 20_000_000;
    private const int MaxRows = 5000;

    private static readonly string[] Required = ["school_code", "employee_code", "full_name", "category"];
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["school_code"] = "school_code", ["رمز المدرسة"] = "school_code",
        ["employee_code"] = "employee_code", ["الرقم الوظيفي"] = "employee_code",
        ["رقم الموظف"] = "employee_code",
        ["full_name"] = "full_name", ["الاسم"] = "full_name", ["اسم الموظف"] = "full_name",
        ["الاسم الكامل"] = "full_name",
        ["category"] = "category", ["الفئة"] = "category", ["فئة الموظف"] = "category",
        ["job_title"] = "job_title", ["المسمى الوظيفي"] = "job_title",
        ["specialization"] = "specialization", ["التخصص"] = "specialization",
        ["qualification"] = "qualification", ["المؤهل"] = "qualification",
        ["gender"] = "gender", ["الجنس"] = "gender"
    };

    public static IReadOnlyList<StaffImportRow> Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new ArgumentException("اختر ملف سجلات موجودا.");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".xlsx" or ".docx"))
            throw new ArgumentException("الصيغ المدعومة لسجلات الكادر: Excel .xlsx أو Word .docx فقط.");
        if (new FileInfo(path).Length is <= 0 or > MaxFileSize)
            throw new ArgumentException("حجم الملف غير مسموح؛ الحد الأقصى 8 ميجابايت.");

        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 200 ||
            archive.Entries.Any(e => e.Length > MaxXmlBytes) ||
            archive.Entries.Sum(e => e.Length) > 40_000_000)
            throw new ArgumentException("الملف المضغوط يحتوي محتوى أكبر من الحد الآمن.");

        var rows = extension == ".docx"
            ? ParseWord(archive)
            : ParseExcel(archive);
        return ReadRows(rows);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseWord(ZipArchive archive)
    {
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var document = ReadXml(archive, "word/document.xml");
        // Only actual tables; body paragraphs cannot be mapped to a school safely.
        foreach (var table in document.Descendants(word + "tbl"))
        {
            var rows = table.Elements(word + "tr")
                .Select(r => (IReadOnlyList<string>)r.Elements(word + "tc")
                    .Select(cell => string.Join(" ", cell.Descendants(word + "p")
                        .Select(p => string.Concat(p.Descendants(word + "t").Select(t => t.Value)))
                        .Where(x => x.Length > 0)).Trim()).ToArray())
                .ToArray();
            if (rows.Length >= 2 && MatchesHeaders(rows[0])) return rows;
        }
        throw new ArgumentException("لم يعثر على جدول Word يحتوي عناوين رمز المدرسة والرقم الوظيفي والاسم والفئة.");
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseExcel(ZipArchive archive)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = archive.GetEntry("xl/sharedStrings.xml") is not null
            ? ReadXml(archive, "xl/sharedStrings.xml").Descendants(ns + "si")
                .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToArray()
            : [];

        var sheetPath = LocateFirstSheet(archive);
        var document = ReadXml(archive, sheetPath);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in document.Descendants(ns + "sheetData").Elements(ns + "row"))
        {
            var values = new SortedDictionary<int, string>();
            var sequential = 0;
            foreach (var cell in row.Elements(ns + "c"))
            {
                var col = ExcelColumn((string?)cell.Attribute("r"), sequential);
                sequential = col + 1;
                if (col >= 30) throw new ArgumentException("عدد أعمدة السجلات في Excel كبير جدا.");
                string value;
                var kind = (string?)cell.Attribute("t");
                if (kind == "inlineStr")
                    value = string.Concat(cell.Descendants(ns + "is").Descendants(ns + "t").Select(x => x.Value));
                else
                {
                    value = (string?)cell.Element(ns + "v") ?? "";
                    if (kind == "s" && value.Length > 0)
                    {
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                            || index < 0 || index >= shared.Length)
                            throw new ArgumentException("فهرس النصوص في ملف Excel غير صالح.");
                        value = shared[index];
                    }
                }
                values[col] = value.Trim();
            }
            if (values.Count == 0) continue;
            var max = values.Keys.Max();
            rows.Add(Enumerable.Range(0, max + 1)
                .Select(i => values.GetValueOrDefault(i, "")).ToArray());
            if (rows.Count > MaxRows + 1)
                throw new ArgumentException("ملف السجلات يحتوي صفوفا أكثر من الحد المسموح.");
        }
        return rows;
    }

    private static string LocateFirstSheet(ZipArchive archive)
    {
        var workbook = archive.GetEntry("xl/workbook.xml");
        var relationships = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbook is null || relationships is null)
            return "xl/worksheets/sheet1.xml"; // minimal OpenXML fixtures

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var first = ReadXml(archive, "xl/workbook.xml").Descendants(ns + "sheet").FirstOrDefault();
        var id = (string?)first?.Attribute(rel + "id")
            ?? throw new ArgumentException("ملف Excel لا يحتوي ورقة عمل.");
        var reference = ReadXml(archive, "xl/_rels/workbook.xml.rels")
            .Descendants().FirstOrDefault(x => (string?)x.Attribute("Id") == id);
        var target = (string?)reference?.Attribute("Target")
            ?? throw new ArgumentException("تعذر العثور على ملف ورقة العمل.");
        var path = target.TrimStart('/');
        if (!path.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) path = "xl/" + path;
        if (!path.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("مسار ورقة Excel غير صالح.");
        return path;
    }

    private static int ExcelColumn(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference)) return fallback;
        int i = 0, col = 0;
        while (i < reference.Length && char.IsAsciiLetterUpper(reference[i]))
        {
            col = col * 26 + reference[i] - 'A' + 1;
            i++;
        }
        return i == 0 ? fallback : col - 1;
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new ArgumentException($"ملف Word أو Excel ناقص: {path}");
        if (entry.Length > MaxXmlBytes) throw new ArgumentException("ملف XML أكبر من الحد المسموح.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlBytes,
            MaxCharactersFromEntities = 0
        });
        return XDocument.Load(reader);
    }

    private static bool MatchesHeaders(IReadOnlyList<string> headers)
    {
        var mapped = headers.Select(h => Headers.GetValueOrDefault(h.Trim(), "")).ToHashSet();
        return Required.All(mapped.Contains);
    }

    private static IReadOnlyList<StaffImportRow> ReadRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2 || !MatchesHeaders(rows[0]))
            throw new ArgumentException("ملف السجلات يحتاج الأعمدة: رمز المدرسة، الرقم الوظيفي، الاسم، الفئة.");
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows[0].Count; i++)
        {
            if (!Headers.TryGetValue(rows[0][i].Trim(), out var name)) continue;
            if (!columns.TryAdd(name, i))
                throw new ArgumentException("تكرر عنوان أحد الأعمدة في الملف.");
        }
        string Get(IReadOnlyList<string> values, string name) =>
            columns.TryGetValue(name, out var index) && index < values.Count
                ? values[index].Trim() : "";
        var result = new List<StaffImportRow>();
        foreach (var row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            result.Add(new StaffImportRow(
                Get(row, "school_code"), Get(row, "employee_code"),
                Get(row, "full_name"), Get(row, "category"),
                Get(row, "job_title"), Get(row, "specialization"),
                Get(row, "qualification"), Get(row, "gender")));
            if (result.Count > MaxRows)
                throw new ArgumentException("ملف السجلات يتجاوز 5000 سجل.");
        }
        if (result.Count == 0) throw new ArgumentException("ملف السجلات لا يحتوي موظفين.");
        return result;
    }
}
