using System.IO;
using System.Data;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using SchoolObservatory.Desktop.Reporting;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private sealed record ReportOption(ReportKind Kind, string Label);
    private sealed record SchoolOption(string? Id, string Label);
    private sealed record StatusOption(string? Code, string Label);

    /// <summary>One reporting panel for district and school, with server-side permission checks.</summary>
    private void ShowReportingModule()
    {
        if (!AccessPolicy.Has(_actor, PermissionFlags.ViewReports) ||
            !AccessPolicy.Has(_actor, PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException("لا تملك صلاحية إصدار التقارير.");
        var engine = new ReportQueryService(_store);
        var root = Stack();
        root.Children.Add(Label("مركز التقارير والتصدير والطباعة", 21, true));
        root.Children.Add(Label(
            "تُقرأ البيانات مباشرة من السجلات المعتمدة حسب الصلاحيات. القوالب الحالية مبدئية وليست نماذج حكومية مصدقة.", 14));
        var descriptions = new[]
        {
            new ReportOption(ReportKind.SchoolDirectory, "دليل المدارس"),
            new ReportOption(ReportKind.StudentEnrollment, "القيد الطلابي — أعداد الصفوف والشعب"),
            new ReportOption(ReportKind.GradesAndSections, "الصفوف والشعب الدراسية"),
            new ReportOption(ReportKind.StudentResults, "نتائج الطلاب — بيانات شخصية"),
            new ReportOption(ReportKind.Attendance, "الحضور والغياب — بيانات شخصية"),
            new ReportOption(ReportKind.StaffDistribution, "توزيع الكادر الوظيفي"),
            new ReportOption(ReportKind.Secondments, "الانتدابات والتكليفات"),
            new ReportOption(ReportKind.ParentCouncil, "أعضاء مجالس الآباء والأمهات"),
            new ReportOption(ReportKind.ApprovedCensus, "الإحصاء المعتمد أو حالة الاعتماد"),
            new ReportOption(ReportKind.DataInconsistencies, "فروقات الإحصاء والسجلات"),
            new ReportOption(ReportKind.MissingInformation, "الحقول الناقصة في ملفات المدارس")
        };
        var options = descriptions.Where(o => o.Kind switch
        {
            ReportKind.StudentEnrollment or ReportKind.GradesAndSections =>
                AccessPolicy.Has(_actor, PermissionFlags.ViewStudents),
            ReportKind.StudentResults or ReportKind.Attendance =>
                AccessPolicy.Has(_actor, PermissionFlags.ViewStudents) &&
                AccessPolicy.Has(_actor, PermissionFlags.ManageStudents),
            ReportKind.StaffDistribution =>
                AccessPolicy.Has(_actor, PermissionFlags.ViewStaff),
            ReportKind.Secondments =>
                AccessPolicy.Has(_actor, PermissionFlags.ManageStaff),
            ReportKind.ParentCouncil =>
                AccessPolicy.Has(_actor, PermissionFlags.ManageSchoolProfile) ||
                AccessPolicy.Has(_actor, PermissionFlags.ManageSchools),
            ReportKind.DataInconsistencies =>
                AccessPolicy.Has(_actor, PermissionFlags.ViewStaff) &&
                AccessPolicy.Has(_actor, PermissionFlags.ViewStudents),
            _ => true
        }).ToArray();
        var type = new ComboBox
        {
            ItemsSource = options, DisplayMemberPath = "Label", Width = 350,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        type.SelectedItem = options.FirstOrDefault(o =>
            o.Kind.ToString() == _navigationContext.Get("reports.kind")) ?? options[0];
        var schools = _store.GetSchools(_actor);
        var schoolChoices = new[] { new SchoolOption(null, "جميع المدارس المصرح بها") }
            .Concat(schools.Select(s => new SchoolOption(s.Id, s.Name))).ToArray();
        var school = new ComboBox
        {
            ItemsSource = schoolChoices, DisplayMemberPath = "Label",
            Width = 340, SelectedIndex = 0
        };
        var zone = Pick(new[] { "الكل" }.Concat(schools.Select(s => s.Zone)
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s)), 180);
        var year = Pick(_store.GetAcademicYears(_actor).Select(s => s.Year).OrderByDescending(s => s), 150);
        year.SelectedItem = _store.AcademicYear;
        var stage = Pick(["الكل", "روضة", "أساسي", "ثانوي", "مجمع تربوي"], 165);
        var grade = Pick(new[] { "الكل" }.Concat(Enumerable.Range(1, 12)
            .Select(n => n.ToString(CultureInfo.InvariantCulture))), 135);
        var section = Pick(["الكل", "أ", "ب", "ج", "د", "هـ", "و", "ز"], 135, editable: true);
        var gender = Pick(["الكل", "ذكور", "إناث"], 135);
        var staffCategory = Pick(["الكل", "معلم", "إداري", "أخصائي اجتماعي"], 175);
        var employment = Pick(["الكل", "موظف", "متعاقد", "متطوع", "غير محدد"], 170, editable: true);
        var assignment = Pick(["الكل", "مباشر", "منتدب", "مكلف", "معار",
            "انتداب", "تكليف", "إعارة", "إجازة", "غير محدد"], 190, editable: true);
        var approval = new ComboBox
        {
            Width = 205, DisplayMemberPath = "Label",
            ItemsSource = new[]
            {
                new StatusOption(null, "المعتمد فقط (افتراضي)"),
                new StatusOption(Statuses.Approved, "معتمدة"),
                new StatusOption(Statuses.Draft, "مسودة - غير معتمد"),
                new StatusOption(Statuses.Submitted, "مقدمة للمراجعة - غير معتمد"),
                new StatusOption(Statuses.SchoolApproved, "مقرّة مدرسيا - غير معتمد"),
                new StatusOption(Statuses.Returned, "معادة للتصحيح - غير معتمد")
            }, SelectedIndex = 0
        };
        var filters = new WrapPanel();
        var typeField = FormField("نوع التقرير", type);
        var schoolField = FormField("المدرسة", school);
        var zoneField = FormField("المنطقة", zone);
        var yearField = FormField("العام الدراسي", year);
        var stageField = FormField("المرحلة", stage);
        var gradeField = FormField("الصف", grade);
        var sectionField = FormField("الشعبة", section);
        var genderField = FormField("جنس الطالب", gender);
        var categoryField = FormField("فئة الكادر", staffCategory);
        var employmentField = FormField("الصفة الوظيفية", employment);
        var assignmentField = FormField("وضع التكليف", assignment);
        var approvalField = FormField("حالة اعتماد الإحصاء", approval);
        foreach (var field in new[] { typeField, schoolField, zoneField,
            yearField, stageField, gradeField, sectionField, genderField,
            categoryField, employmentField, assignmentField, approvalField })
            filters.Children.Add(field);

        static ComboBox Pick(IEnumerable<string> values, int width, bool editable = false)
        {
            var list = values.ToArray();
            return new ComboBox
            {
                Width = width, ItemsSource = list, SelectedIndex = list.Length > 0 ? 0 : -1,
                IsEditable = editable
            };
        }
        static string? FilterText(ComboBox input)
        {
            var value = input.IsEditable ? input.Text : input.SelectedItem?.ToString();
            return string.IsNullOrWhiteSpace(value) || value == "الكل" ? null : value.Trim();
        }

        ReportKind CurrentKind() => (type.SelectedItem as ReportOption)?.Kind ??
            ReportKind.SchoolDirectory;
        void UpdateRelevantFilters()
        {
            var kind = CurrentKind();
            var isStudent = kind is ReportKind.StudentEnrollment or ReportKind.GradesAndSections
                or ReportKind.StudentResults or ReportKind.Attendance;
            var isStaff = kind is ReportKind.StaffDistribution or ReportKind.Secondments;
            gradeField.Visibility = isStudent || kind == ReportKind.ApprovedCensus
                ? Visibility.Visible : Visibility.Collapsed;
            sectionField.Visibility = isStudent ? Visibility.Visible : Visibility.Collapsed;
            genderField.Visibility = isStudent || kind == ReportKind.ApprovedCensus
                ? Visibility.Visible : Visibility.Collapsed;
            categoryField.Visibility = isStaff ? Visibility.Visible : Visibility.Collapsed;
            employmentField.Visibility = isStaff ? Visibility.Visible : Visibility.Collapsed;
            assignmentField.Visibility = isStaff ? Visibility.Visible : Visibility.Collapsed;
            approvalField.Visibility = kind == ReportKind.ApprovedCensus
                ? Visibility.Visible : Visibility.Collapsed;
            var schoolSelection = (school.SelectedItem as SchoolOption)?.Id;
            var selectedSchool = schools.FirstOrDefault(s => s.Id == schoolSelection);
            if (selectedSchool is not null && selectedSchool.Gender != "مختلطة" && isStudent)
                genderField.Visibility = Visibility.Collapsed;
            year.IsEnabled = kind is ReportKind.StudentEnrollment or ReportKind.GradesAndSections
                or ReportKind.StudentResults or ReportKind.Attendance or ReportKind.ApprovedCensus;
            if (!year.IsEnabled) year.SelectedItem = _store.AcademicYear;
            // Only official status selector changes a report's approval classification.
            _navigationContext.Set("reports.kind", kind.ToString());
        }
        type.SelectionChanged += (_, _) => UpdateRelevantFilters();
        school.SelectionChanged += (_, _) => UpdateRelevantFilters();
        UpdateRelevantFilters();

        root.Children.Add(Box(filters));
        root.Children.Add(Label("تخصيص القالب المبدئي للطباعة (لا يغير بيانات التقرير أو اعتماده)", 15, true));
        var printHeader = Field(width: 310);
        printHeader.MaxLength = 120;
        var printFooter = Field(width: 310);
        printFooter.MaxLength = 80;
        var orientation = Pick(["تلقائي حسب الأعمدة", "عمودي A4", "أفقي A4"], 175);
        root.Children.Add(Box(LineOfFields(
            ("نص إضافي أعلى التقرير (اختياري)", printHeader),
            ("نص إضافي أسفل التقرير (اختياري)", printFooter),
            ("اتجاه الصفحة", orientation))));
        var action = new WrapPanel();
        var run = Btn("عرض التقرير وتطبيق المرشحات", (_, _) => { }, tone: "primary");
        var csv = Btn("تصدير CSV", (_, _) => { }, false, "secondary");
        var excel = Btn("تصدير Excel XLSX", (_, _) => { }, false, "success");
        var preview = Btn("معاينة PDF والطباعة", (_, _) => { }, false, "secondary");
        foreach (var element in new[] { run, csv, excel, preview })
            action.Children.Add(element);
        root.Children.Add(action);
        var progress = Label("", 14, true);
        progress.Foreground = new SolidColorBrush(Color.FromRgb(5, 90, 125));
        root.Children.Add(progress);
        var summary = Label("اختر التقرير واضغط عرض. تصدير الملفات متاح بعد التحقق من الصلاحيات.", 14);
        root.Children.Add(Box(summary, new Thickness(12)));
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, MinHeight = 160,
            MaxHeight = 470, SelectionUnit = DataGridSelectionUnit.FullRow,
            CanUserSortColumns = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(Box(TableSurface(grid, "لا توجد سجلات تطابق المرشحات.")));
        ReportRequest? lastRequest = null;
        ReportTable? lastTable = null;
        bool working = false;

        ReportRequest CreateRequest()
        {
            var kind = CurrentKind();
            var visibleStudents = kind is ReportKind.StudentEnrollment or ReportKind.GradesAndSections
                or ReportKind.StudentResults or ReportKind.Attendance;
            var isStaff = kind is ReportKind.StaffDistribution or ReportKind.Secondments;
            return new ReportRequest(kind, new ReportFilter(
                SchoolId: (school.SelectedItem as SchoolOption)?.Id,
                Zone: FilterText(zone), AcademicYear: FilterText(year),
                Stage: FilterText(stage),
                Grade: (visibleStudents || kind == ReportKind.ApprovedCensus) &&
                    int.TryParse(FilterText(grade), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var gradeNumber) ? gradeNumber : null,
                Section: visibleStudents ? FilterText(section) : null,
                Gender: genderField.Visibility == Visibility.Visible ? FilterText(gender) : null,
                StaffCategory: isStaff ? FilterText(staffCategory) : null,
                EmploymentType: isStaff ? FilterText(employment) : null,
                AssignmentStatus: isStaff ? FilterText(assignment) : null,
                ApprovalStatus: kind == ReportKind.ApprovedCensus
                    ? (approval.SelectedItem as StatusOption)?.Code : null));
        }

        void ShowTable(ReportTable result)
        {
            grid.Columns.Clear();
            for (var c = 0; c < result.Columns.Count; c++)
            {
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = result.Columns[c],
                    Binding = new Binding("Cells[" + c.ToString(CultureInfo.InvariantCulture) + "]"),
                    IsReadOnly = true, MinWidth = 125, MaxWidth = 370
                });
            }
            grid.ItemsSource = result.Rows;
            summary.Text = result.Title + " | " + result.StatusLabel +
                " | عدد السجلات: " + result.RecordCount.ToString("N0") +
                " | " + string.Join(" | ", result.Totals.Select(t => t.Key + "=" + t.Value.ToString("N0")));
            csv.IsEnabled = excel.IsEnabled = preview.IsEnabled = true;
        }

        async Task<ReportTable> Execute(bool showTable)
        {
            var request = CreateRequest();
            working = true;
            run.IsEnabled = csv.IsEnabled = excel.IsEnabled = preview.IsEnabled = false;
            progress.Text = "جار قراءة السجلات والتحقق من الصلاحيات، لا تغلق الملف أثناء التصدير...";
            try
            {
                var table = await Task.Run(() => engine.Generate(_actor, request));
                lastRequest = request;
                lastTable = table;
                if (showTable && ReferenceEquals(ContentHost.Content, root))
                    ShowTable(table);
                return table;
            }
            finally
            {
                working = false;
                run.IsEnabled = true;
                csv.IsEnabled = excel.IsEnabled = preview.IsEnabled = lastTable is not null;
                progress.Text = "";
            }
        }

        // Buttons are created with the existing UI factory. Attach additional handlers
        // after the factory's empty handler, keeping their styles and focus semantics.
        run.Click += async (_, _) =>
        {
            if (working) return;
            try { await Execute(showTable: true); }
            catch (Exception ex) { progress.Text = "تعذر إنشاء التقرير: " + ex.Message; }
        };
        excel.Click += async (_, _) =>
        {
            if (working) return;
            var save = new SaveFileDialog
            {
                Filter = "Excel XLSX (*.xlsx)|*.xlsx",
                FileName = "SchoolObservatory-" + CurrentKind() + ".xlsx"
            };
            if (save.ShowDialog() != true) return;
            try
            {
                var result = await Execute(showTable: true);
                var target = save.FileName;
                await Task.Run(() => WriteAtomic(target, stream => XlsxReportWriter.Write(stream, result)));
                progress.Text = "تم حفظ ملف Excel بصيغته الصحيحة، مع حماية من صيغ Excel غير الآمنة.";
            }
            catch (Exception ex) { progress.Text = "تعذر حفظ Excel: " + ex.Message; }
        };
        csv.Click += async (_, _) =>
        {
            if (working) return;
            var save = new SaveFileDialog
            {
                Filter = "CSV UTF-8 (*.csv)|*.csv",
                FileName = "SchoolObservatory-" + CurrentKind() + ".csv"
            };
            if (save.ShowDialog() != true) return;
            try
            {
                var report = await Execute(showTable: true);
                await Task.Run(() => WriteAtomic(save.FileName, stream =>
                {
                    using var text = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
                    // Preserve classification, provenance and filters in every CSV
                    // so an unapproved snapshot cannot masquerade as an official one.
                    foreach (var metadata in new[]
                    {
                        new[] { "عنوان التقرير", report.Title },
                        new[] { "الجهة", report.Institution },
                        new[] { "العام الدراسي", report.AcademicYear },
                        new[] { "تاريخ الإنشاء", report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture) },
                        new[] { "صفة الاعتماد", report.StatusLabel },
                        new[] { "نوع القالب", "قالب مبدئي غير معتمد كوثيقة حكومية" },
                        new[] { "عدد السجلات", report.RecordCount.ToString(CultureInfo.InvariantCulture) },
                        new[] { "المرشحات", string.Join(" | ", report.AppliedFilters) }
                    })
                        text.WriteLine(string.Join(",", metadata.Select(CsvCell)));
                    text.WriteLine();
                    text.WriteLine(string.Join(",", report.Columns.Select(CsvCell)));
                    foreach (var row in report.Rows)
                        text.WriteLine(string.Join(",", row.Cells.Select(CsvCell)));
                }));
                progress.Text = "تم حفظ CSV مع حماية خلايا الصيغ.";
            }
            catch (Exception ex) { progress.Text = "تعذر حفظ CSV: " + ex.Message; }
        };
        preview.Click += async (_, _) =>
        {
            if (working) return;
            try
            {
                var table = await Execute(showTable: true);
                var settings = new ReportPrintOptions(
                    printHeader.Text.Trim(), printFooter.Text.Trim(),
                    orientation.SelectedIndex switch { 1 => false, 2 => true, _ => null });
                var dialog = new ReportPreviewWindow(table, settings) { Owner = this };
                WindowLayout.FitToWorkArea(dialog);
                dialog.ShowDialog();
            }
            catch (Exception ex) { progress.Text = "تعذر معاينة التقرير: " + ex.Message; }
        };

        Display("التقارير والتصدير", root);
    }

    /// <summary>File replacement occurs only after the complete document has been written.</summary>
    private static void WriteAtomic(string destination, Action<Stream> writer)
    {
        var temp = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
                writer(file);
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
