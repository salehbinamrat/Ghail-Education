using System.IO;
using System.Threading;
using PdfSharp.Pdf.IO;
using SchoolObservatory.Core;
using SchoolObservatory.Desktop.Reporting;
using Xunit;

namespace SchoolObservatory.ReportingTests;

/// <summary>Runs real WPF text shaping and PDFSharp raster/PDF output on Windows STA.</summary>
public sealed class PdfArabicPhase6Tests
{
    [Fact]
    public void ArabicA4PdfIsCompletePaginatedAndOpensInStandardPdfReader()
    {
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try
            {
                var rows = Enumerable.Range(1, 140)
                    .Select(n => new ReportRow([
                        "مدرسة غيل بن يمين - العليب",
                        "طالب " + n + " عبدالله بن سالم",
                        "أولى",
                        n % 2 == 0 ? "ب" : "أ",
                        "العام الدراسي 2026/2027 - نتيجة تعليمية",
                        n.ToString(),
                        "100"
                    ]))
                    .ToArray();
                var report = new ReportTable(ReportKind.StudentResults,
                    "نتائج الطلاب", "إدارة التربية والتعليم بمديرية غيل بن يمين",
                    "2026/2027", new DateTimeOffset(2026, 11, 18, 9, 0, 0,
                        TimeSpan.FromHours(3)), "بيانات تشغيلية - قالب مبدئي غير رسمي",
                    false, false,
                    ["المدرسة", "اسم الطالب", "الصف", "الشعبة", "التفاصيل", "الدرجة", "النهاية العظمى"],
                    rows, new Dictionary<string, long> { ["results"] = rows.Length },
                    ["المنطقة: العليب", "العام: 2026/2027"]);
                var doc = ReportDocumentRenderer.Build(report);
                Assert.True(doc.Pages.Count >= 2);
                using var stream = new MemoryStream();
                PdfReportWriter.Write(doc, stream);
                Assert.True(stream.Length > 4000);
                stream.Position = 0;
                using var parsed = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                Assert.Equal(doc.Pages.Count, parsed.PageCount);
                Assert.InRange(parsed.Pages[0].Width.Point, 820, 850); // A4 landscape
                Assert.InRange(parsed.Pages[0].Height.Point, 580, 620);
            }
            catch (Exception ex) { error = ex; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromMinutes(3)), "Arabic PDF renderer timed out.");
        if (error is not null) throw new Xunit.Sdk.XunitException(
            "PDF rendering failed: " + error);
    }

    [Fact]
    public void ProvisionalReportNeverGetsOfficialSeal()
    {
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try
            {
                var report = new ReportTable(ReportKind.ApprovedCensus,
                    "ملخص الاستمارات الإحصائية", "الإدارة", "2026/2027",
                    DateTimeOffset.Now, "بيانات إحصائية غير معتمدة", false,
                    false, ["المدرسة"], [new ReportRow(["العليب"])],
                    new Dictionary<string, long>(), []);
                var doc = ReportDocumentRenderer.Build(report);
                Assert.Single(doc.Pages);
                using var stream = new MemoryStream();
                PdfReportWriter.Write(doc, stream);
                Assert.True(stream.Length > 1000);
            }
            catch (Exception ex) { error = ex; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromMinutes(1)), "PDF generation timed out.");
        if (error is not null) throw new Xunit.Sdk.XunitException(
            "Provisional PDF generation failed: " + error);
    }
}
