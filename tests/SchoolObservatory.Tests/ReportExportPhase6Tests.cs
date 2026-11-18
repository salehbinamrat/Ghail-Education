using System.Text;
using ClosedXML.Excel;
using SchoolObservatory.Core;
using SchoolObservatory.Desktop;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class ReportExportPhase6Tests
{
    private static EducationalReport ArabicSample() => new(
        "كشف الطلاب", "إدارة التربية والتعليم - مديرية غيل بن يمين", "2026/2027",
        false, ["اسم الطالب", "ملاحظات", "العدد"],
        [new ReportRecord(["أحمد بن سالم", "=HYPERLINK(\"https://example.test\")", "3"]),
         new ReportRecord(["فاطمة محمد", "اختبار عربي", "4"])],
        new Dictionary<string, string> { ["المدرسة"] = "مدرسة العليب" },
        new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(3)));

    [Fact]
    public void ArabicSpreadsheetOpensAndPotentialFormulasArePlainText()
    {
        var path = Path.Combine(Path.GetTempPath(), "p6-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            ReportFileExporter.SaveExcel(ArabicSample(), path);
            using var book = new XLWorkbook(path);
            var sheet = Assert.Single(book.Worksheets);
            Assert.True(sheet.RightToLeft);
            Assert.Equal("كشف الطلاب", sheet.Cell(1, 1).GetString());
            Assert.Equal("أحمد بن سالم", sheet.Cell(8, 1).GetString());
            Assert.StartsWith("'", sheet.Cell(8, 2).GetString());
            Assert.True(string.IsNullOrEmpty(sheet.Cell(8, 2).FormulaA1));
            Assert.Equal("فاطمة محمد", sheet.Cell(9, 1).GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ArabicPdfHasValidSignatureAndNonEmptyLayout()
    {
        var path = Path.Combine(Path.GetTempPath(), "p6-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            ReportFileExporter.SavePdf(ArabicSample(), path);
            Assert.True(new FileInfo(path).Length > 700);
            var header = new byte[5];
            using (var file = File.OpenRead(path))
                Assert.Equal(5, file.Read(header, 0, header.Length));
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(header));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
