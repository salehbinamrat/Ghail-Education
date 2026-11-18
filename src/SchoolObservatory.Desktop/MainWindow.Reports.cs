using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SchoolObservatory.Core;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private void ShowOfficialReports()
    {
        if (!AccessPolicy.Has(_actor, PermissionFlags.ViewReports))
            throw new UnauthorizedAccessException("ليس لديك صلاحية عرض التقارير.");
        var root = Stack();
        root.Children.Add(Label("التقارير الرسمية والتشغيلية — تصدير PDF / Excel ومعاينة الطباعة", 18, true));
        root.Children.Add(Label(
            "النماذج الحالية قابلة للتهيئة وليست نماذج حكومية معتمدة. لن يحمل التقرير صفة الاعتماد إلا للإحصاء النهائي المعتمد.",
            13));
        var schools = _store.GetSchools(_actor);
        var kinds = new KeyValuePair<string, EducationalReportKind>[]
        {
            new("دليل المدارس", EducationalReportKind.SchoolDirectory),
            new("قيد الطلاب", EducationalReportKind.StudentEnrollment),
            new("الصفوف والشعب", EducationalReportKind.GradesAndSections),
            new("نتائج الطلاب", EducationalReportKind.StudentResults),
            new("الحضور والغياب", EducationalReportKind.Attendance),
            new("توزيع الكادر", EducationalReportKind.StaffDistribution),
            new("الانتدابات والتكليفات", EducationalReportKind.PersonnelAssignments),
            new("مجالس الآباء والأمهات", EducationalReportKind.ParentCouncils),
            new("الإحصاء المعتمد", EducationalReportKind.ApprovedCensus),
            new("تعارضات ونواقص البيانات", EducationalReportKind.DataQuality)
        };
        ComboBox Select(IEnumerable<string> values, double width = 175)
        {
            var combo = new ComboBox
            {
                Width = width, MinHeight = 38, Margin = new Thickness(5),
                FontWeight = FontWeights.Bold, FlowDirection = FlowDirection.RightToLeft
            };
            combo.Items.Add("الكل");
            foreach (var val in values.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct().OrderBy(x => x))
                combo.Items.Add(val);
            combo.SelectedIndex = 0;
            return combo;
        }
        var kind = new ComboBox { Width = 230, MinHeight = 38, Margin = new Thickness(5),
            DisplayMemberPath = "Key", SelectedValuePath = "Value",
            ItemsSource = kinds, SelectedIndex = 0 };
        var school = new ComboBox { Width = 230, MinHeight = 38, Margin = new Thickness(5),
            DisplayMemberPath = "Name", SelectedValuePath = "Id" };
        school.Items.Add(new School("", "", "كل المدارس", "", "", "", true));
        foreach (var item in schools) school.Items.Add(item);
        school.SelectedIndex = 0;
        var zone = Select(schools.Select(s => s.Zone));
        var stage = Select(schools.Select(s => s.Stage));
        var year = Select(_store.GetAcademicYears(_actor).Select(y => y.Year));
        year.SelectedItem = _store.AcademicYear;
        var grade = Select(Enumerable.Range(1, 12).Select(i => i.ToString(CultureInfo.InvariantCulture)), 110);
        var section = new TextBox { Width = 110, MinHeight = 38, MaxLength = 30,
            Margin = new Thickness(5), ToolTip = "اترك الشعبة فارغة لعرض الكل" };
        var gender = Select(["ذكور", "إناث"], 130);
        var staffCategory = Select(["معلم", "إداري", "أخصائي"], 150);
        var employment = Select(["رسمي", "متعاقد", "غير محدد"], 150);
        var assignment = Select(["مباشر", "منتدب", "مكلف", "معار", "غير محدد"], 155);
        var census = Select(["approved"], 130);

        string? Pick(ComboBox combo) => combo.SelectedItem is string s && s != "الكل" ? s : null;
        var panel = new WrapPanel { FlowDirection = FlowDirection.RightToLeft };
        void AddFilter(string title, Control control)
        {
            var group = new StackPanel { Margin = new Thickness(3), Orientation = Orientation.Vertical };
            group.Children.Add(Label(title, 13, true));
            group.Children.Add(control);
            panel.Children.Add(group);
        }
        AddFilter("نوع التقرير", kind); AddFilter("المدرسة", school);
        AddFilter("المنطقة", zone); AddFilter("العام الدراسي", year);
        AddFilter("المرحلة", stage); AddFilter("الصف", grade);
        AddFilter("الشعبة", section); AddFilter("الجنس", gender);
        AddFilter("فئة الكادر", staffCategory); AddFilter("التوظيف", employment);
        AddFilter("التكليف", assignment); AddFilter("اعتماد الإحصاء", census);
        root.Children.Add(Box(panel));

        var summary = Label("", 15, true);
        var tableHost = new ContentControl();
        root.Children.Add(summary);
        root.Children.Add(tableHost);
        EducationalReport? shown = null;
        EducationalReportFilter CurrentFilter()
        {
            var selectedSchool = school.SelectedItem as School;
            var selectedGrade = Pick(grade);
            return new EducationalReportFilter(
                (EducationalReportKind)kind.SelectedValue,
                string.IsNullOrEmpty(selectedSchool?.Id) ? null : selectedSchool.Id,
                Pick(zone), Pick(year), Pick(stage),
                int.TryParse(selectedGrade, out var g) ? g : null,
                string.IsNullOrWhiteSpace(section.Text) ? null : section.Text.Trim(),
                Pick(gender), Pick(staffCategory), Pick(employment), Pick(assignment),
                (EducationalReportKind)kind.SelectedValue == EducationalReportKind.ApprovedCensus
                    ? Pick(census) : null);
        }
        void Refresh()
        {
            // Fresh authenticated reads on every preview/export. Never reuse stale privilege scopes.
            var report = new OfficialReportService(_store).Generate(_actor, CurrentFilter());
            shown = report;
            summary.Text = $"{report.Title} | {report.ApprovalLabel} | إجمالي السجلات: {report.RecordTotal}";
            var grid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, CanUserDeleteRows = false,
                FlowDirection = FlowDirection.RightToLeft, MinHeight = 280,
                ItemsSource = report.Rows
            };
            for (var i = 0; i < report.Columns.Count; i++)
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = report.Columns[i], Binding = new Binding($"Values[{i}]"),
                    MinWidth = 115, Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                });
            tableHost.Content = TableSurface(grid, "لا توجد سجلات مطابقة للمرشحات.");
        }
        var buttons = new WrapPanel { FlowDirection = FlowDirection.RightToLeft };
        buttons.Children.Add(Btn("تحديث ومعاينة التقرير", (_, _) => Safe(Refresh)));
        void Export(bool pdf, bool preview)
        {
            // Regenerate at the exact time of disclosure to enforce current permissions.
            var report = new OfficialReportService(_store).Generate(_actor, CurrentFilter());
            if (preview)
            {
                var directory = Path.Combine(Path.GetTempPath(), "Ghail-Education-ReportPreviews");
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pdf");
                ReportFileExporter.SavePdf(report, file);
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                Info("تم فتح معاينة PDF. يمكنك الطباعة من عارض PDF بعد مراجعة الصفحات.");
                return;
            }
            var dialog = new SaveFileDialog
            {
                Filter = pdf ? "PDF (*.pdf)|*.pdf" : "Excel (*.xlsx)|*.xlsx",
                DefaultExt = pdf ? ".pdf" : ".xlsx",
                FileName = pdf ? "Ghail-Education-Report.pdf" : "Ghail-Education-Report.xlsx",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true) return;
            if (pdf) ReportFileExporter.SavePdf(report, dialog.FileName);
            else ReportFileExporter.SaveExcel(report, dialog.FileName);
            Info("اكتمل حفظ التقرير في المسار المحدد.");
        }
        buttons.Children.Add(Btn("تصدير Excel XLSX", (_, _) => Safe(() => Export(false, false))));
        buttons.Children.Add(Btn("تصدير PDF", (_, _) => Safe(() => Export(true, false))));
        buttons.Children.Add(Btn("معاينة قبل الطباعة", (_, _) => Safe(() => Export(true, true))));
        root.Children.Add(buttons);
        Display("التقارير والتصدير", root);
        Refresh();
    }
}
