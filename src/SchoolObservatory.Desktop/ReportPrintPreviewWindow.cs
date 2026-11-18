using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

/// <summary>
/// Print preview stays inside the signed-in application. Sensitive rosters are
/// not written to a shared temporary folder just to render a preview.
/// </summary>
public sealed class ReportPrintPreviewWindow : Window
{
    public ReportPrintPreviewWindow(EducationalReport report)
    {
        report.Validate();
        Title = "معاينة الطباعة — " + report.Title;
        Width = 1150;
        Height = 820;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        var document = new FlowDocument
        {
            FlowDirection = FlowDirection.RightToLeft,
            FontFamily = new FontFamily("Arial"),
            FontSize = 11,
            PageWidth = 1122, // ISO A4 landscape in 96 DPI-independent pixels
            PageHeight = 794,
            PagePadding = new Thickness(36),
            ColumnWidth = 1050
        };
        document.Blocks.Add(new Paragraph(new Run(report.Title))
        {
            FontSize = 19, FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center
        });
        document.Blocks.Add(new Paragraph(new Run(report.Institution))
        { FontSize = 14, TextAlignment = TextAlignment.Center });
        document.Blocks.Add(new Paragraph(new Run(
            $"العام الدراسي: {report.AcademicYear} | {report.ApprovalLabel}"))
        { FontWeight = FontWeights.Bold });
        document.Blocks.Add(new Paragraph(new Run(
            $"تاريخ الإنشاء: {report.GeneratedAt:yyyy-MM-dd HH:mm} | إجمالي السجلات: {report.RecordTotal}")));
        document.Blocks.Add(new Paragraph(new Run(
            "المرشحات: " + (report.AppliedFilters.Count == 0 ? "بدون" :
                string.Join("، ", report.AppliedFilters.Select(x => x.Key + ": " + x.Value))))));
        var table = new Table { CellSpacing = 0, BorderBrush = Brushes.SlateGray,
            BorderThickness = new Thickness(0.7) };
        foreach (var _ in report.Columns)
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var header = new TableRow();
        foreach (var name in report.Columns)
            header.Cells.Add(Cell(name, true));
        group.Rows.Add(header);
        foreach (var record in report.Rows)
        {
            var row = new TableRow();
            foreach (var value in record.Values)
                row.Cells.Add(Cell(value, false));
            group.Rows.Add(row);
        }
        document.Blocks.Add(table);
        document.Blocks.Add(new Paragraph(new Run("نموذج قابل للتهيئة؛ لا يعد اعتمادا حكوميا"))
        { FontSize = 10, Foreground = Brushes.DimGray });
        Content = new DocumentViewer { Document = document,
            FlowDirection = FlowDirection.RightToLeft };
    }

    private static TableCell Cell(string value, bool heading)
    {
        var cell = new TableCell(new Paragraph(new Run(value))
        { Margin = new Thickness(0), TextAlignment = TextAlignment.Right })
        {
            Padding = new Thickness(5),
            BorderBrush = Brushes.LightSlateGray,
            BorderThickness = new Thickness(0, 0, 0.5, 0.5),
            Background = heading ? Brushes.LightBlue : Brushes.White
        };
        if (heading) cell.FontWeight = FontWeights.Bold;
        return cell;
    }
}
