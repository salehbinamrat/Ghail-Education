using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop.Reporting;

/// <summary>
/// One deterministic A4 WPF layout for PDF, print preview and real printers.
/// Windows DirectWrite shapes connected Arabic glyphs and wraps RTL cells before pagination.
/// </summary>
public static class ReportDocumentRenderer
{
    private const double A4Width = 793.7;  // 210 mm * 96/25.4
    private const double A4Height = 1122.5; // 297 mm * 96/25.4
    private const double Margin = 44;
    private static readonly FontFamily Arial = new("Arial");
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(17, 58, 81));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(61, 91, 108));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(6, 104, 145));
    private static readonly Brush Pale = new SolidColorBrush(Color.FromRgb(227, 242, 249));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(197, 220, 230));

    public static FixedDocument Build(ReportTable report, ReportPrintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        report.Validate();
        options ??= new ReportPrintOptions();
        options.Validate();
        if (report.Rows.Count > 5000)
            throw new InvalidOperationException(
                "معاينة وطباعة أكثر من 5000 صف تتطلب تصفية السجلات؛ يمكن تصدير الجدول الكامل إلى Excel.");
        var landscape = options.Landscape ?? (report.Columns.Count > 6);
        var width = landscape ? A4Height : A4Width;
        var height = landscape ? A4Width : A4Height;
        var inside = width - 2 * Margin;
        var colWidth = inside / report.Columns.Count;
        var pages = new List<FixedPage>();
        FixedPage? activePage = null;
        double cursor = 0;
        const double bottomReserved = 70;

        void NewPage()
        {
            var page = new FixedPage
            {
                Width = width, Height = height, Background = Brushes.White,
                FlowDirection = FlowDirection.RightToLeft, FontFamily = Arial
            };
            var y = Margin;
            if (!string.IsNullOrWhiteSpace(options.HeaderNote))
                y += AddText(page, options.HeaderNote, Margin, y, inside, 11, Muted, true) + 6;
            y += AddText(page, report.Institution, Margin, y, inside, 15,
                Ink, true) + 6;
            y += AddText(page, report.Title, Margin, y, inside, 21,
                Accent, true) + 5;
            y += AddText(page,
                "العام الدراسي: " + report.AcademicYear + "    |    أنشئ في: " +
                report.GeneratedAt.ToString("yyyy-MM-dd HH:mm zzz",
                    System.Globalization.CultureInfo.InvariantCulture),
                Margin, y, inside, 11, Muted, true) + 5;
            y += AddText(page, report.StatusLabel, Margin, y, inside, 11,
                report.IsApprovedSource ? Accent : new SolidColorBrush(Color.FromRgb(159, 38, 59)),
                true) + 5;
            var filterLine = report.AppliedFilters.Count == 0
                ? "المرشحات: الكل" : "المرشحات: " + string.Join(" | ", report.AppliedFilters);
            y += AddText(page, filterLine, Margin, y, inside, 10, Muted, true) + 6;
            y += AddText(page,
                $"عدد السجلات: {report.RecordCount:N0}    |    قالب مبدئي غير معتمد كوثيقة حكومية",
                Margin, y, inside, 10, Muted, true) + 12;
            if (y + 85 >= height - bottomReserved)
                throw new InvalidOperationException("مرشحات التقرير طويلة ولا تترك مساحة للجدول.");
            var headerHeight = RowHeight(report.Columns, colWidth, true);
            AddRow(page, report.Columns, Margin, y, colWidth, headerHeight, true, 0);
            cursor = y + headerHeight;
            activePage = page;
            pages.Add(page);
        }

        NewPage();
        var index = 0;
        foreach (var row in report.Rows)
        {
            var required = RowHeight(row.Cells, colWidth, false);
            var lastY = height - bottomReserved;
            if (required > lastY - (Margin + 195))
                throw new InvalidOperationException(
                    "تحتوي إحدى خلايا التقرير على نص أطول من مساحة A4؛ ضيق الأعمدة أو المرشحات ولا تقص النص.");
            if (cursor + required > lastY) NewPage();
            AddRow(activePage!, row.Cells, Margin, cursor,
                colWidth, required, false, index++);
            cursor += required;
        }

        if (report.Rows.Count == 0)
        {
            AddText(activePage!, "لا توجد بيانات مطابقة للمرشحات المختارة.",
                Margin, cursor + 22, inside, 14, Muted, true);
        }
        if (report.Totals.Count > 0)
        {
            var text = "الإجماليات: " + string.Join(" | ",
                report.Totals.Select(p => p.Key + ": " + p.Value.ToString("N0")));
            var needed = Measure(text, inside, 11) + 20;
            if (cursor + needed > height - bottomReserved) NewPage();
            AddText(activePage!, text, Margin, cursor + 6, inside, 11, Accent, true);
        }

        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(width, height);
        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var footerY = height - 45;
            if (!string.IsNullOrWhiteSpace(options.FooterNote))
                AddText(p, options.FooterNote, Margin, footerY - 16, inside, 9, Muted, true);
            AddText(p, "المرصد المدرسي  |  " + report.StatusLabel, Margin,
                footerY, inside * 0.75, 9, Muted, true);
            AddText(p, $"صفحة {i + 1} من {pages.Count}",
                Margin + inside * 0.75, footerY, inside * 0.25, 9, Ink, true);
            p.Measure(new Size(width, height));
            p.Arrange(new Rect(0, 0, width, height));
            p.UpdateLayout();
            document.Pages.Add(new PageContent { Child = p });
        }
        return document;
    }

    private static double Measure(string value, double width, double fontSize)
    {
        var text = CreateText(value, width, fontSize, Ink, false);
        text.Measure(new Size(width, double.PositiveInfinity));
        return Math.Ceiling(text.DesiredSize.Height);
    }

    private static TextBlock CreateText(string? value, double width, double size,
        Brush foreground, bool bold)
    {
        return new TextBlock
        {
            Text = value ?? "", Width = Math.Max(20, width), FontFamily = Arial,
            FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold,
            FontSize = size, Foreground = foreground,
            FlowDirection = FlowDirection.RightToLeft,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static double AddText(FixedPage page, string value,
        double left, double top, double width, double size,
        Brush foreground, bool bold)
    {
        var text = CreateText(value, width, size, foreground, bold);
        text.Measure(new Size(width, double.PositiveInfinity));
        FixedPage.SetLeft(text, left);
        FixedPage.SetTop(text, top);
        page.Children.Add(text);
        return Math.Ceiling(text.DesiredSize.Height);
    }

    private static double RowHeight(IReadOnlyList<string> cells, double width, bool header)
    {
        var size = header ? 11.3 : 10.8;
        return Math.Max(header ? 40 : 34, cells.Max(c =>
            Measure(c ?? "", Math.Max(24, width - 14), size) + 16));
    }

    private static void AddRow(FixedPage page, IReadOnlyList<string> cells, double left,
        double y, double cellWidth, double height, bool header, int index)
    {
        for (var j = 0; j < cells.Count; j++)
        {
            var box = new Border
            {
                Width = cellWidth, Height = height, BorderBrush = Line,
                BorderThickness = new Thickness(0.7),
                Background = header ? Pale : index % 2 == 0 ? Brushes.White :
                    new SolidColorBrush(Color.FromRgb(245, 250, 252)),
                Padding = new Thickness(7, 4, 7, 4),
                FlowDirection = FlowDirection.RightToLeft
            };
            box.Child = CreateText(cells[j], Math.Max(20, cellWidth - 14),
                header ? 11.3 : 10.8, header ? Accent : Ink, true);
            FixedPage.SetLeft(box, left + (cells.Count - 1 - j) * cellWidth);
            FixedPage.SetTop(box, y);
            page.Children.Add(box);
        }
    }
}

/// <summary>Export rendered A4 preview pages to universally readable PDF, offline.</summary>
public static class PdfReportWriter
{
    public static void Write(FixedDocument document, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("تعذر كتابة ملف PDF.");
        using var pdf = new PdfDocument();
        foreach (var content in document.Pages)
        {
            var page = content.Child ??
                throw new InvalidOperationException("صفحة المعاينة غير متاحة.");
            page.Measure(new Size(page.Width, page.Height));
            page.Arrange(new Rect(0, 0, page.Width, page.Height));
            page.UpdateLayout();
            const double dpi = 144;
            var width = (int)Math.Ceiling(page.Width * dpi / 96);
            var height = (int)Math.Ceiling(page.Height * dpi / 96);
            var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(page);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            png.Position = 0;
            using var image = XImage.FromStream(png);
            var sheet = pdf.AddPage();
            sheet.Width = PdfSharp.Drawing.XUnit.FromPoint(page.Width * 72 / 96);
            sheet.Height = PdfSharp.Drawing.XUnit.FromPoint(page.Height * 72 / 96);
            using var gfx = XGraphics.FromPdfPage(sheet);
            gfx.DrawImage(image, 0, 0, sheet.Width.Point, sheet.Height.Point);
        }
        pdf.Save(destination, false);
    }
}
