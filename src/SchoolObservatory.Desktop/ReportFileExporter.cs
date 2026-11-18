using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

/// <summary>Provisional A4 templates. Official government forms are not represented as approved.</summary>
public static class ReportFileExporter
{
    public static void SaveExcel(EducationalReport report, string filePath)
    {
        report.Validate();
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("تقرير");
        sheet.RightToLeft = true;
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        var n = report.Columns.Count;
        sheet.Range(1, 1, 1, n).Merge();
        sheet.Cell(1, 1).SetValue(ReportTextSafety.AsSpreadsheetText(report.Title));
        sheet.Range(1, 1, 1, n).Style.Font.Bold = true;
        sheet.Range(1, 1, 1, n).Style.Font.FontSize = 18;
        sheet.Range(1, 1, 1, n).Style.Fill.BackgroundColor = XLColor.FromHtml("#DCEEF6");
        sheet.Range(2, 1, 2, n).Merge();
        sheet.Cell(2, 1).SetValue(ReportTextSafety.AsSpreadsheetText(report.Institution));
        sheet.Range(3, 1, 3, n).Merge();
        sheet.Cell(3, 1).SetValue($"العام الدراسي: {report.AcademicYear}  |  {report.ApprovalLabel}");
        sheet.Range(4, 1, 4, n).Merge();
        sheet.Cell(4, 1).SetValue($"تاريخ التوليد: {report.GeneratedAt:yyyy-MM-dd HH:mm}  |  إجمالي السجلات: {report.RecordTotal}");
        sheet.Range(5, 1, 5, n).Merge();
        sheet.Cell(5, 1).SetValue(ReportTextSafety.AsSpreadsheetText(
            "المرشحات: " + (report.AppliedFilters.Count == 0 ? "بدون" :
                string.Join("، ", report.AppliedFilters.Select(k => k.Key + ": " + k.Value)))));
        const int header = 7;
        for (var c = 0; c < n; c++)
            sheet.Cell(header, c + 1).SetValue(ReportTextSafety.AsSpreadsheetText(report.Columns[c]));
        var headings = sheet.Range(header, 1, header, n);
        headings.Style.Font.Bold = true;
        headings.Style.Fill.BackgroundColor = XLColor.FromHtml("#196D92");
        headings.Style.Font.FontColor = XLColor.White;
        for (var r = 0; r < report.Rows.Count; r++)
        {
            for (var c = 0; c < n; c++)
            {
                // Explicitly use text values. Never call FormulaA1 or FormulaR1C1.
                var cell = sheet.Cell(header + r + 1, c + 1);
                cell.SetValue(ReportTextSafety.AsSpreadsheetText(report.Rows[r].Values[c]));
            }
        }
        sheet.Range(header, 1, header + Math.Max(1, report.Rows.Count), n)
            .Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        sheet.SheetView.FreezeRows(header);
        sheet.Columns(1, n).AdjustToContents(1, Math.Max(header, header + report.Rows.Count));
        foreach (var column in sheet.Columns(1, n))
        {
            if (column.Width > 48) column.Width = 48;
            if (column.Width < 13) column.Width = 13;
        }
        sheet.Range(1, 1, header + Math.Max(1, report.Rows.Count), n)
            .Style.Alignment.WrapText = true;
        sheet.PageSetup.PagesWide = 1;
        sheet.Style.Font.FontName = "Arial";
        sheet.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        sheet.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.PageSetup.Footer.Right.AddText("صفحة &P من &N");
        sheet.PageSetup.Header.Center.AddText(report.Title);
        workbook.SaveAs(filePath);
    }

    public static void SavePdf(EducationalReport report, string filePath)
    {
        report.Validate();
        // The Community license is permitted only when the organization meets its terms.
        // Configure the license explicitly before production use if necessary.
        QuestPDF.Settings.License = LicenseType.Community;
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(22);
                page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(10));
                page.ContentFromRightToLeft();
                page.Header().Column(header =>
                {
                    header.Item().AlignCenter().Text(report.Title).Bold().FontSize(17);
                    header.Item().AlignCenter().Text(report.Institution).FontSize(11);
                    header.Item().Text($"العام الدراسي: {report.AcademicYear} | {report.ApprovalLabel}").Bold();
                    header.Item().Text($"تاريخ التوليد: {report.GeneratedAt:yyyy-MM-dd HH:mm} | إجمالي السجلات: {report.RecordTotal}");
                    header.Item().Text("المرشحات: " + (report.AppliedFilters.Count == 0
                        ? "بدون" : string.Join("، ", report.AppliedFilters.Select(x => x.Key + ": " + x.Value))));
                    header.Item().PaddingBottom(8).LineHorizontal(1);
                });
                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        for (var i = 0; i < report.Columns.Count; i++) columns.RelativeColumn();
                    });
                    table.Header(header =>
                    {
                        foreach (var name in report.Columns)
                            header.Cell().Background("#DCEEF6").Border(0.5f)
                                .Padding(4).Text(name).Bold().FontSize(9);
                    });
                    foreach (var row in report.Rows)
                    {
                        foreach (var value in row.Values)
                            table.Cell().BorderBottom(0.5f).BorderColor("#D5DFE5")
                                .Padding(4).Text(value).FontSize(9);
                    }
                });
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("صفحة ");
                    text.CurrentPageNumber();
                    text.Span(" من ");
                    text.TotalPages();
                    text.Span(" | نموذج تقارير قابل للتهيئة");
                });
            });
        }).GeneratePdf(filePath);
    }
}
