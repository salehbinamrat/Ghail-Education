using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace SchoolObservatory.Core;

/// <summary>
/// A zero-extra-dependency SpreadsheetML writer. All untrusted strings are literal inlineStr
/// values (never formulas); selected aggregate columns get numeric cells.
/// </summary>
public static class XlsxReportWriter
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly HashSet<string> NumericTitles = new(StringComparer.Ordinal)
    {
        "العدد", "الطلاب", "الذكور", "الإناث", "الإجمالي",
        "المعلمون", "المدارس", "الشعب", "الحضور", "الغياب", "الدرجة",
        "النهاية العظمى", "عدد المدارس", "عدد الموظفين", "عدد الطلاب",
        "الفصول", "الموظفون", "السجلات", "إجمالي الطلاب"
    };

    public static void Write(Stream destination, ReportTable report)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(report);
        if (!destination.CanWrite) throw new ArgumentException("مجرى الكتابة غير متاح.");
        report.Validate();
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        Part(zip, "[Content_Types].xml", x =>
        {
            x.WriteStartElement("Types", ContentTypes);
            Default(x, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(x, "xml", "application/xml");
            Override(x, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(x, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            Override(x, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            x.WriteEndElement();
        });
        Part(zip, "_rels/.rels", x =>
        {
            x.WriteStartElement("Relationships", PackageRel);
            Relationship(x, "rId1", OfficeRel + "/officeDocument", "xl/workbook.xml");
            x.WriteEndElement();
        });
        Part(zip, "xl/_rels/workbook.xml.rels", x =>
        {
            x.WriteStartElement("Relationships", PackageRel);
            Relationship(x, "rId1", OfficeRel + "/worksheet", "worksheets/sheet1.xml");
            Relationship(x, "rId2", OfficeRel + "/styles", "styles.xml");
            x.WriteEndElement();
        });
        Part(zip, "xl/workbook.xml", x =>
        {
            x.WriteStartElement("workbook", MainNs);
            x.WriteAttributeString("xmlns", "r", null, OfficeRel);
            x.WriteStartElement("sheets");
            x.WriteStartElement("sheet");
            x.WriteAttributeString("name", "التقرير");
            x.WriteAttributeString("sheetId", "1");
            x.WriteAttributeString("r", "id", OfficeRel, "rId1");
            x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        });
        Part(zip, "xl/styles.xml", WriteStyles);
        Part(zip, "xl/worksheets/sheet1.xml", x => WriteSheet(x, report));
    }

    private static void WriteSheet(XmlWriter x, ReportTable report)
    {
        x.WriteStartElement("worksheet", MainNs);
        x.WriteStartElement("sheetViews");
        x.WriteStartElement("sheetView");
        x.WriteAttributeString("workbookViewId", "0");
        x.WriteAttributeString("rightToLeft", "1");
        x.WriteAttributeString("showGridLines", "0");
        x.WriteStartElement("pane");
        x.WriteAttributeString("ySplit", "8");
        x.WriteAttributeString("topLeftCell", "A9");
        x.WriteAttributeString("state", "frozen");
        x.WriteAttributeString("activePane", "bottomLeft");
        x.WriteEndElement();
        x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("sheetFormatPr");
        x.WriteAttributeString("defaultRowHeight", "23");
        x.WriteEndElement();
        x.WriteStartElement("cols");
        for (var c = 1; c <= report.Columns.Count; c++)
        {
            x.WriteStartElement("col");
            x.WriteAttributeString("min", c.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("max", c.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("width", "23");
            x.WriteAttributeString("customWidth", "1");
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteStartElement("sheetData");
        Row(x, 1, [report.Title], 1);
        Row(x, 2, [report.Institution], 2);
        Row(x, 3, ["العام الدراسي: " + report.AcademicYear,
                   "تاريخ الإنشاء: " + report.GeneratedAt.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)], 2);
        Row(x, 4, [report.StatusLabel + " — قالب مبدئي غير معتمد كوثيقة حكومية"], 2);
        Row(x, 5, [report.AppliedFilters.Count == 0 ? "المرشحات: الكل" :
            "المرشحات: " + string.Join(" | ", report.AppliedFilters)], 2);
        Row(x, 6, ["عدد السجلات: " + report.RecordCount.ToString(CultureInfo.InvariantCulture)], 2);
        Row(x, 8, report.Columns, 3);
        for (var i = 0; i < report.Rows.Count; i++)
        {
            var n = i + 9;
            x.WriteStartElement("row");
            x.WriteAttributeString("r", n.ToString(CultureInfo.InvariantCulture));
            for (var c = 0; c < report.Columns.Count; c++)
            {
                var value = report.Rows[i].Cells[c];
                bool numeric = NumericTitles.Contains(report.Columns[c]) &&
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                Cell(x, n, c, value, i % 2 == 0 ? 4 : 0, numeric);
            }
            x.WriteEndElement();
        }
        if (report.Totals.Count > 0)
            Row(x, report.Rows.Count + 10,
                ["الإجماليات: " + string.Join(" | ", report.Totals.Select(x =>
                    x.Key + ": " + x.Value.ToString(CultureInfo.InvariantCulture)))], 3);
        x.WriteEndElement(); // sheetData
        x.WriteStartElement("autoFilter");
        x.WriteAttributeString("ref", "A8:" + Column(report.Columns.Count - 1) +
            Math.Max(9, report.Rows.Count + 8).ToString(CultureInfo.InvariantCulture));
        x.WriteEndElement();
        x.WriteStartElement("printOptions"); x.WriteAttributeString("horizontalCentered", "1"); x.WriteEndElement();
        x.WriteStartElement("pageMargins");
        foreach (var (attr, value) in new[] { ("left", "0.3"), ("right", "0.3"),
            ("top", "0.6"), ("bottom", "0.6"), ("header", "0.25"), ("footer", "0.25") })
            x.WriteAttributeString(attr, value);
        x.WriteEndElement();
        x.WriteStartElement("pageSetup");
        x.WriteAttributeString("paperSize", "9"); // ISO A4
        x.WriteAttributeString("orientation", report.Columns.Count >= 7 ? "landscape" : "portrait");
        x.WriteAttributeString("fitToWidth", "1");
        x.WriteAttributeString("fitToHeight", "0");
        x.WriteEndElement();
        x.WriteStartElement("headerFooter");
        x.WriteElementString("oddHeader", "&C" + report.Institution);
        x.WriteElementString("oddFooter", "&C" + "صفحة &P من &N");
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Row(XmlWriter x, int number, IReadOnlyList<string> cells, int style)
    {
        x.WriteStartElement("row");
        x.WriteAttributeString("r", number.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < cells.Count; i++) Cell(x, number, i, cells[i], style, false);
        x.WriteEndElement();
    }

    private static void Cell(XmlWriter x, int row, int column, string? raw, int style, bool numeric)
    {
        var value = raw ?? "";
        if (value.Length > 32767)
            throw new ArgumentException("نص خلية Excel يتجاوز الحد المسموح.");
        x.WriteStartElement("c");
        x.WriteAttributeString("r", Column(column) + row.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        if (numeric)
        {
            x.WriteAttributeString("t", "n");
            x.WriteElementString("v", value);
        }
        else
        {
            // XLSX inline string is never executable. The explicit leading apostrophe
            // additionally protects users who copy the cell into CSV or other sheets.
            var trimmed = value.TrimStart(' ', '\t', '\r', '\n');
            if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))
                value = "'" + value;
            x.WriteAttributeString("t", "inlineStr");
            x.WriteStartElement("is");
            x.WriteElementString("t", value);
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    private static string Column(int number)
    {
        number++;
        var text = "";
        while (number > 0) { number--; text = (char)('A' + number % 26) + text; number /= 26; }
        return text;
    }

    private static void WriteStyles(XmlWriter x)
    {
        x.WriteStartElement("styleSheet", MainNs);
        x.WriteStartElement("fonts"); x.WriteAttributeString("count", "2");
        for (var i = 0; i < 2; i++)
        {
            x.WriteStartElement("font");
            x.WriteStartElement("b"); x.WriteEndElement();
            x.WriteStartElement("sz"); x.WriteAttributeString("val", i == 0 ? "12" : "15"); x.WriteEndElement();
            x.WriteStartElement("name"); x.WriteAttributeString("val", "Arial"); x.WriteEndElement();
            x.WriteStartElement("color"); x.WriteAttributeString("rgb", "FF123B56"); x.WriteEndElement();
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteStartElement("fills"); x.WriteAttributeString("count", "3");
        x.WriteStartElement("fill"); x.WriteStartElement("patternFill");
        x.WriteAttributeString("patternType", "none"); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("fill"); x.WriteStartElement("patternFill");
        x.WriteAttributeString("patternType", "gray125"); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("fill"); x.WriteStartElement("patternFill");
        x.WriteAttributeString("patternType", "solid");
        x.WriteStartElement("fgColor"); x.WriteAttributeString("rgb", "FFDEF2FA"); x.WriteEndElement();
        x.WriteStartElement("bgColor"); x.WriteAttributeString("indexed", "64"); x.WriteEndElement();
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("borders"); x.WriteAttributeString("count", "1");
        x.WriteStartElement("border");
        foreach (var name in new[] { "left", "right", "top", "bottom", "diagonal" })
        { x.WriteStartElement(name); x.WriteEndElement(); }
        x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("cellStyleXfs"); x.WriteAttributeString("count", "1");
        x.WriteStartElement("xf"); x.WriteAttributeString("numFmtId", "0");
        x.WriteAttributeString("fontId", "0"); x.WriteAttributeString("fillId", "0");
        x.WriteAttributeString("borderId", "0"); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("cellXfs"); x.WriteAttributeString("count", "5");
        for (var i = 0; i < 5; i++)
        {
            x.WriteStartElement("xf");
            x.WriteAttributeString("numFmtId", "0");
            x.WriteAttributeString("fontId", i == 1 ? "1" : "0");
            x.WriteAttributeString("fillId", i is 3 or 4 ? "2" : "0");
            x.WriteAttributeString("borderId", "0");
            x.WriteAttributeString("xfId", "0");
            x.WriteAttributeString("applyAlignment", "1");
            x.WriteStartElement("alignment");
            x.WriteAttributeString("horizontal", "right");
            x.WriteAttributeString("vertical", "center");
            x.WriteAttributeString("readingOrder", "2");
            x.WriteAttributeString("wrapText", "1");
            x.WriteEndElement();
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteStartElement("cellStyles"); x.WriteAttributeString("count", "1");
        x.WriteStartElement("cellStyle"); x.WriteAttributeString("name", "Normal");
        x.WriteAttributeString("xfId", "0"); x.WriteAttributeString("builtinId", "0");
        x.WriteEndElement(); x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Part(ZipArchive zip, string path, Action<XmlWriter> writer)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false
        });
        xml.WriteStartDocument();
        writer(xml);
        xml.WriteEndDocument();
    }

    private static void Default(XmlWriter x, string extension, string mime)
    {
        x.WriteStartElement("Default");
        x.WriteAttributeString("Extension", extension);
        x.WriteAttributeString("ContentType", mime);
        x.WriteEndElement();
    }
    private static void Override(XmlWriter x, string name, string mime)
    {
        x.WriteStartElement("Override");
        x.WriteAttributeString("PartName", name);
        x.WriteAttributeString("ContentType", mime);
        x.WriteEndElement();
    }
    private static void Relationship(XmlWriter x, string id, string type, string target)
    {
        x.WriteStartElement("Relationship");
        x.WriteAttributeString("Id", id);
        x.WriteAttributeString("Type", type);
        x.WriteAttributeString("Target", target);
        x.WriteEndElement();
    }
}
