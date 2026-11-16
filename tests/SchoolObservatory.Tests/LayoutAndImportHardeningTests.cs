using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SchoolObservatory.Core;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class LayoutAndImportHardeningTests
{
    private static string RepositoryFile(params string[] fragments)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SchoolObservatory.sln")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("SchoolObservatory.sln not found.");
        return Path.Combine(new[] { root.FullName }.Concat(fragments).ToArray());
    }

    [Fact]
    public void TablesAllowHorizontalScrollingAndColumnResizeWithVirtualization()
    {
        var doc = XDocument.Load(RepositoryFile("src", "SchoolObservatory.Desktop", "App.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var style = Assert.Single(doc.Descendants(p + "Style").Where(s =>
            (string?)s.Attribute("TargetType") == "DataGrid"));
        var setters = style.Elements(p + "Setter").ToDictionary(
            s => (string?)s.Attribute("Property") ?? "", s => (string?)s.Attribute("Value") ?? "");
        Assert.Equal("Auto", setters["HorizontalScrollBarVisibility"]);
        Assert.Equal("True", setters["EnableRowVirtualization"]);
        Assert.Equal("True", setters["EnableColumnVirtualization"]);
        Assert.Equal("True", setters["CanUserResizeColumns"]);
    }

    [Fact]
    public void MainHeaderDoesNotOverlapItsTitleAndAcademicYear()
    {
        var doc = XDocument.Load(RepositoryFile("src", "SchoolObservatory.Desktop", "MainWindow.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var header = Assert.Single(doc.Descendants(p + "Grid").Where(g =>
            (string?)g.Attribute(x + "Name") == "HeaderLayout"));
        Assert.Equal(2, header.Element(p + "Grid.ColumnDefinitions")?.Elements(p + "ColumnDefinition").Count());
        Assert.Contains(header.Elements(p + "TextBlock"), t =>
            (string?)t.Attribute(x + "Name") == "SectionTitle" && (string?)t.Attribute("Grid.Column") == "0");
        Assert.Contains(header.Elements(p + "TextBlock"), t =>
            (string?)t.Attribute(x + "Name") == "YearText" && (string?)t.Attribute("Grid.Column") == "1");
    }

    [Fact]
    public void ExcelFormulaCachedValuesCannotBeImportedAsSchoolIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), "formula-school-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                Write(archive, "xl/worksheets/sheet1.xml", """
                    <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                     <sheetData>
                      <row r="1"><c r="A1" t="inlineStr"><is><t>school_code</t></is></c>
                       <c r="B1" t="inlineStr"><is><t>employee_code</t></is></c>
                       <c r="C1" t="inlineStr"><is><t>full_name</t></is></c>
                       <c r="D1" t="inlineStr"><is><t>category</t></is></c></row>
                      <row r="2"><c r="A2" t="str"><f>CONCAT("GH","-01")</f><v>GH-01</v></c>
                       <c r="B2" t="inlineStr"><is><t>23</t></is></c>
                       <c r="C2" t="inlineStr"><is><t>حسن</t></is></c>
                       <c r="D2" t="inlineStr"><is><t>معلم</t></is></c></row>
                     </sheetData>
                    </worksheet>
                    """);
            Assert.Throws<ArgumentException>(() => SchoolStaffImportParser.Parse(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WordMergedCellsCannotShiftSchoolOrPersonnelColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), "merged-school-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                Write(archive, "word/document.xml", """
                    <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                     <w:body><w:tbl>
                      <w:tr><w:tc><w:p><w:r><w:t>رمز المدرسة</w:t></w:r></w:p></w:tc>
                       <w:tc><w:p><w:r><w:t>الرقم الوظيفي</w:t></w:r></w:p></w:tc>
                       <w:tc><w:p><w:r><w:t>الاسم</w:t></w:r></w:p></w:tc>
                       <w:tc><w:p><w:r><w:t>الفئة</w:t></w:r></w:p></w:tc></w:tr>
                      <w:tr><w:tc><w:tcPr><w:gridSpan w:val="2"/></w:tcPr>
                       <w:p><w:r><w:t>GH-01</w:t></w:r></w:p></w:tc>
                       <w:tc><w:p><w:r><w:t>44</w:t></w:r></w:p></w:tc>
                       <w:tc><w:p><w:r><w:t>أحمد</w:t></w:r></w:p></w:tc>
                       <w:tc><w:p><w:r><w:t>معلم</w:t></w:r></w:p></w:tc></w:tr>
                     </w:tbl></w:body>
                    </w:document>
                    """);
            Assert.Throws<ArgumentException>(() => SchoolStaffImportParser.Parse(path));
        }
        finally { File.Delete(path); }
    }

    private static void Write(ZipArchive archive, string name, string xml)
    {
        using var output = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        output.Write(xml);
    }
}
