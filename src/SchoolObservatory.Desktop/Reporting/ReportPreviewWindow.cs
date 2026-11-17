using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop.Reporting;

/// <summary>Always preview the exact FixedDocument used by real printing and PDF export.</summary>
public sealed class ReportPreviewWindow : Window
{
    private readonly ReportTable _report;
    private readonly FixedDocument _document;
    private readonly TextBlock _status = new()
    {
        FontFamily = new System.Windows.Media.FontFamily("Arial"),
        FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8), FontSize = 13
    };

    public ReportPreviewWindow(ReportTable report, ReportPrintOptions? options = null)
    {
        _report = report;
        _document = ReportDocumentRenderer.Build(report, options);
        Title = "معاينة الطباعة - " + report.Title;
        Width = 1060; Height = 780; MinWidth = 660; MinHeight = 460;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = new System.Windows.Media.FontFamily("Arial");
        FontWeight = FontWeights.Bold;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(12, 8, 12, 8) };
        var print = new Button { Content = "طباعة التقرير", MinWidth = 140 };
        print.Click += (_, _) =>
        {
            try
            {
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() == true)
                {
                    dialog.PrintDocument(_document.DocumentPaginator, _report.Title);
                    _status.Text = "أرسل التقرير إلى الطابعة المختارة.";
                }
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        var pdf = new Button { Content = "حفظ PDF", MinWidth = 140 };
        pdf.Click += async (_, _) =>
        {
            var picker = new SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf", DefaultExt = ".pdf",
                FileName = "SchoolObservatory-" + _report.Kind + ".pdf"
            };
            if (picker.ShowDialog(this) != true) return;
            var path = picker.FileName;
            pdf.IsEnabled = false;
            print.IsEnabled = false;
            _status.Text = "جار إنشاء PDF باللغة العربية...";
            try
            {
                // Every WPF visual belongs to its creating STA dispatcher. Do the PDF
                // conversion on the UI thread, yielding first for a visible progress label.
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var file = File.Create(temp))
                        PdfReportWriter.Write(_document, file);
                    File.Move(temp, path, overwrite: true);
                    _status.Text = "تم حفظ PDF بنجاح.";
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (Exception ex) { _status.Text = "تعذر إنشاء PDF: " + ex.Message; }
            finally { pdf.IsEnabled = true; print.IsEnabled = true; }
        };
        bar.Children.Add(print); bar.Children.Add(pdf); bar.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Add(bar);
        var viewer = new DocumentViewer
        {
            Document = _document,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Thickness(8)
        };
        layout.Children.Add(viewer);
        Content = layout;
    }
}
