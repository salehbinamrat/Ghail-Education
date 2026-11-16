using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolObservatory.Core;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop;

public partial class MainWindow : Window
{
    public bool LogoutRequested { get; private set; }
    private readonly SqliteStore _store;
    private readonly UserAccount _actor;
    private readonly Brush _primary = new SolidColorBrush(Color.FromRgb(0, 148, 177));
    private readonly Brush _neutral = new SolidColorBrush(Color.FromRgb(236, 243, 251));
    private readonly Brush _muted = new SolidColorBrush(Color.FromRgb(89, 111, 139));

    public MainWindow(SqliteStore store, UserAccount actor)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowLayout.FitToWorkArea(this);
        _store = store;
        _actor = actor;
        AccountText.Text = $"{actor.Username}  |  {(actor.IsOwner ? "المسؤول المؤسس" : RoleName(actor.Role))}";
        YearText.Text = $"العام الدراسي: {store.AcademicYear}";
        AddNav("لوحة التحكم", ShowDashboard);
        AddNav("دليل المدارس", ShowSchools);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewStaff))
            AddNav("كادر المدارس والسجلات", ShowStaff);
        if (AccessPolicy.Has(actor, PermissionFlags.EditCensus) || AccessPolicy.Has(actor, PermissionFlags.ViewReports)) AddNav("إحصاء الطلاب والمعلمين", ShowCensus);
        if (AccessPolicy.Has(actor, PermissionFlags.ApproveSchool) || AccessPolicy.Has(actor, PermissionFlags.ApproveDistrict) || AccessPolicy.Has(actor, PermissionFlags.ReviewCensus)) AddNav("مراجعة واعتماد الإحصاء", ShowReviews);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewReports)) AddNav("التقارير والتصدير", ShowReports);
        if (AccessPolicy.CanCreateUsers(actor)) AddNav("إدارة المستخدمين", ShowUsers);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewAudit)) AddNav("سجل العمليات", ShowAudit);
        ShowDashboard();
    }

    private static string RoleName(string role) => role switch
    {
        Roles.Admin => "مدير النظام", Roles.Statistician => "مسؤول الإحصاء",
        Roles.Reviewer => "مراجع الإحصاء", Roles.Principal => "مدير المدرسة",
        Roles.Entry => "مدخل البيانات", Roles.Viewer => "مشاهد", _ => role
    };

    private static string StatusName(string status) => status switch
    {
        Statuses.Draft => "مسودة", Statuses.Submitted => "قيد مراجعة المدرسة",
        Statuses.SchoolApproved => "قيد اعتماد المديرية", Statuses.Approved => "معتمدة",
        Statuses.Returned => "أعيدت للتصحيح", _ => status
    };

    private void AddNav(string text, Action action)
    {
        var button = new Button
        {
            Content = text, Background = new SolidColorBrush(Color.FromRgb(9, 107, 151)),
            BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(2, 3, 2, 3)
        };
        button.Click += (_, _) => Safe(action);
        NavPanel.Children.Add(button);
    }

    private void Display(string title, UIElement body)
    {
        SectionTitle.Text = title;
        ContentHost.Content = body;
    }

    private static StackPanel Stack(int margin = 0) => new()
    {
        Orientation = Orientation.Vertical,
        Margin = new Thickness(margin)
    };

    private static TextBlock Label(string text, int size = 15, bool bold = false) => new()
    {
        Text = text, FontSize = size,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        Margin = new Thickness(3, 6, 3, 6), TextWrapping = TextWrapping.Wrap
    };

    private static TextBox Field(string? value = null, double width = 200) => new()
    {
        Text = value ?? "", MinWidth = width, Width = width, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button Btn(string title, RoutedEventHandler click, bool allowed = true)
    {
        var btn = new Button { Content = title, IsEnabled = allowed, Margin = new Thickness(3, 6, 3, 6) };
        btn.Click += click;
        return btn;
    }

    private static Border Box(UIElement child, Thickness? padding = null) => new()
    {
        Child = child, Padding = padding ?? new Thickness(17), Background = Brushes.White,
        CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 16),
        BorderBrush = new SolidColorBrush(Color.FromRgb(224, 232, 240)), BorderThickness = new Thickness(1)
    };

    private static DataGrid GridFor<T>(IEnumerable<T> data, params (string Header, string Property, bool Editable)[] cols)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, ItemsSource = data, MinHeight = 150, MaxHeight = 420,
            IsReadOnly = cols.All(x => !x.Editable), SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Star)
        };
        foreach (var col in cols)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = col.Header, IsReadOnly = !col.Editable,
                Binding = new Binding(col.Property) { Mode = col.Editable ? BindingMode.TwoWay : BindingMode.OneWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
            });
        }
        return grid;
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "المرصد المدرسي — تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void Info(string text) => MessageBox.Show(text, "المرصد المدرسي", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        LogoutRequested = true;
        Close();
    }

    private void ShowDashboard()
    {
        var data = _store.GetDashboard(_actor);
        var root = Stack();
        root.Children.Add(Label($"أهلا بك في { _store.DistrictName } — نظرة عامة على البيانات المعتمدة", 17));
        var tiles = new WrapPanel { Margin = new Thickness(0, 12, 0, 14) };
        foreach (var (title, count) in new (string, int)[] {
            ("المدارس النشطة", data.Schools), ("الإحصاءات المعتمدة", data.Approved),
            ("قيد المراجعة", data.Submitted), ("إجمالي الطلاب", data.Students),
            ("إجمالي المعلمين", data.Teachers), ("الفصول", data.Classrooms) })
        {
            var tile = Stack();
            tile.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = _muted });
            tile.Children.Add(new TextBlock { Text = count.ToString("N0", CultureInfo.CurrentCulture),
                FontSize = 26, FontWeight = FontWeights.Bold, Foreground = _primary, Margin = new Thickness(0, 9, 0, 0) });
            var card = Box(tile);
            card.Width = 220; card.Margin = new Thickness(0, 0, 12, 12);
            tiles.Children.Add(card);
        }
        root.Children.Add(tiles);
        root.Children.Add(Box(Label("تُحتسب أعداد الطلاب والمعلمين والفصول من الاستمارات المعتمدة فقط. الاستمارات غير المعتمدة لا تدخل في المجموع الرسمي.", 15)));
        root.Children.Add(Box(Label("تنبيه أمني: النسخة الأولى محلية وغير مشفرة بعد؛ لا تستخدم بيانات شخصية حساسة حتى اكتمال متطلبات الإنتاج.", 14)));
        root.Children.Add(Btn("تحديث المؤشرات", (_, _) => ShowDashboard()));
        Display("لوحة التحكم", root);
    }

    private void ShowSchools()
    {
        var root = Stack();
        var data = _store.GetSchools(_actor);
        root.Children.Add(Label($"عدد المدارس الظاهرة: {data.Count} — حسب الصلاحيات المحددة لحسابك", 15));
        var grid = GridFor(data,
            ("الرمز", "Code", false), ("المدرسة", "Name", false), ("المنطقة", "Zone", false),
            ("الملكية", "Ownership", false), ("النوع", "Gender", false),
            ("المرحلة", "Stage", false), ("نشطة", "IsActive", false));
        root.Children.Add(Box(grid));
        if (AccessPolicy.CanEditSchool(_actor))
        {
            var form = Stack();
            form.Children.Add(Label("إضافة مدرسة أو تعديل المدرسة المحددة", 17, true));
            var code = Field(width: 150); var name = Field(width: 300); var zone = Field(width: 210);
            var ownership = new ComboBox { Width = 160, ItemsSource = new[] { "حكومية", "أهلية" }, SelectedIndex = 0 };
            var stage = new ComboBox { Width = 160, ItemsSource = new[] { "أساسي", "ثانوي", "مجمع تربوي" }, SelectedIndex = 0 };
            var gender = new ComboBox { Width = 160, ItemsSource = new[] { "ذكور", "إناث", "مختلطة" }, SelectedIndex = 2 };
            var active = new CheckBox { Content = "المدرسة نشطة", IsChecked = true, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
            var fields = new WrapPanel();
            foreach (var (title, control) in new (string, UIElement)[] {
                ("رمز المدرسة",code),("اسم المدرسة",name),("المنطقة",zone),
                ("الملكية",ownership),("نوع المدرسة",gender),("المرحلة",stage) })
            {
                var group = Stack(); group.Children.Add(Label(title, 13)); group.Children.Add(control); fields.Children.Add(group);
            }
            form.Children.Add(fields);
            form.Children.Add(active);
            School? selected = null;
            grid.SelectionChanged += (_, _) =>
            {
                selected = grid.SelectedItem as School;
                if (selected is null) return;
                code.Text = selected.Code; name.Text = selected.Name; zone.Text = selected.Zone;
                ownership.SelectedItem = selected.Ownership; stage.SelectedItem = selected.Stage;
                gender.SelectedItem = selected.Gender;
                active.IsChecked = selected.IsActive;
            };
            var buttons = new WrapPanel();
            buttons.Children.Add(Btn("إضافة مدرسة جديدة", (_, _) => Safe(() =>
            {
                _store.AddSchool(_actor, code.Text, name.Text, zone.Text, ownership.SelectedItem?.ToString() ?? "", stage.SelectedItem?.ToString() ?? "", gender.SelectedItem?.ToString() ?? "مختلطة");
                Info("تمت إضافة المدرسة."); ShowSchools();
            })));
            buttons.Children.Add(Btn("حفظ تعديل المدرسة المحددة", (_, _) => Safe(() =>
            {
                if (selected is null) throw new InvalidOperationException("حدد المدرسة المطلوب تعديلها من الجدول.");
                _store.UpdateSchool(_actor, selected with
                {
                    Code = code.Text, Name = name.Text, Zone = zone.Text,
                    Ownership = ownership.SelectedItem?.ToString() ?? "",
                    Stage = stage.SelectedItem?.ToString() ?? "", Gender = gender.SelectedItem?.ToString() ?? "مختلطة",
                    IsActive = active.IsChecked == true
                });
                Info("تم حفظ بيانات المدرسة."); ShowSchools();
            })));
            buttons.Children.Add(Btn("استيراد مدارس من Excel CSV", (_, _) => Safe(() =>
            {
                var file = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv", Title = "اختر قائمة المدارس بصيغة CSV UTF-8" };
                if (file.ShowDialog() != true) return;
                if (new FileInfo(file.FileName).Length > 5_000_000)
                    throw new ArgumentException("حجم الملف يتجاوز 5 ميجابايت.");
                IReadOnlyList<SchoolImport> entries;
                try { entries = SchoolCsvParser.ParseUtf8(File.ReadAllBytes(file.FileName)); }
                catch (DecoderFallbackException)
                {
                    throw new ArgumentException("تعذر قراءة الملف بترميز UTF-8 الصحيح. احفظ قائمة المدارس بصيغة CSV UTF-8 من Excel ثم حاول مجددا.");
                }
                if (MessageBox.Show($"سيتم إضافة {entries.Count} مدرسة. هل تريد المتابعة؟",
                    "تأكيد الاستيراد", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var imported = _store.ImportSchools(_actor, entries);
                Info($"تم استيراد {imported} مدرسة دون أخطاء."); ShowSchools();
            })));
            buttons.Children.Add(Btn("مسح الحقول", (_, _) =>
            {
                selected = null; grid.SelectedItem = null; code.Text = ""; name.Text = "";
                zone.Text = ""; active.IsChecked = true;
                ownership.SelectedIndex = 0; stage.SelectedIndex = 0; gender.SelectedIndex = 2;
            }));
            form.Children.Add(buttons);
            root.Children.Add(Box(form));
        }
        else if ((_actor.Role is Roles.Entry or Roles.Principal) && data.Count == 1 &&
                 AccessPolicy.CanEditSchoolProfile(_actor, data[0].Id))
        {
            var school = data[0];
            var form = Stack();
            form.Children.Add(Label("تعديل مرحلة ونوع مدرستك فقط", 16, true));
            form.Children.Add(Label("لا يمكنك تغيير رمز المدرسة أو اسمها أو موقعها أو بيانات أي مدرسة أخرى.", 13));
            var stage = new ComboBox { Width = 180,
                ItemsSource = new[] { "أساسي", "ثانوي", "مجمع تربوي" },
                SelectedItem = school.Stage, HorizontalAlignment = HorizontalAlignment.Left };
            var gender = new ComboBox { Width = 180,
                ItemsSource = new[] { "ذكور", "إناث", "مختلطة" },
                SelectedItem = school.Gender, HorizontalAlignment = HorizontalAlignment.Left };
            form.Children.Add(Label("المرحلة", 13)); form.Children.Add(stage);
            form.Children.Add(Label("نوع المدرسة", 13)); form.Children.Add(gender);
            form.Children.Add(Btn("حفظ بيانات مدرستي", (_, _) => Safe(() =>
            {
                _store.UpdateOwnSchoolProfile(_actor, school.Id,
                    stage.SelectedItem?.ToString() ?? "", gender.SelectedItem?.ToString() ?? "");
                Info("تم تحديث نوع المدرسة والمرحلة."); ShowSchools();
            })));
            root.Children.Add(Box(form));
        }
        Display("دليل المدارس", root);
    }

    private void ShowStaff()
    {
        if (!AccessPolicy.Has(_actor, PermissionFlags.ViewStaff))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض كادر المدارس.");
        var schools = _store.GetSchools(_actor).Where(s => s.IsActive).ToList();
        var root = Stack();
        root.Children.Add(Label("سجلات المعلمين والطاقم الإداري والأخصائيين الاجتماعيين", 18, true));
        root.Children.Add(Label("كل حساب مدرسة يرى سجلات مدرسته فقط. أما مسؤولو المديرية فيرون المدارس وفقا لصلاحياتهم.", 13));
        if (schools.Count == 0)
        {
            root.Children.Add(Label("لا توجد مدرسة نشطة متاحة لهذا الحساب."));
            Display("كادر المدارس والسجلات", root);
            return;
        }

        var schoolBox = new ComboBox
        {
            Width = 380, ItemsSource = schools, DisplayMemberPath = "Name",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(Label("المدرسة", 14, true));
        root.Children.Add(schoolBox);

        var staffGrid = GridFor(Array.Empty<SchoolStaff>(),
            ("الرقم الوظيفي", "EmployeeCode", false),
            ("الاسم الكامل", "FullName", false),
            ("الفئة", "Category", false),
            ("المسمى", "JobTitle", false),
            ("التخصص", "Specialization", false),
            ("المؤهل", "Qualification", false),
            ("الجنس", "Gender", false),
            ("نشط", "IsActive", false));
        staffGrid.MaxHeight = 370;
        root.Children.Add(Box(staffGrid));

        var editor = Stack();
        editor.Children.Add(Label("إضافة موظف أو تعديل السجل المحدد", 16, true));
        var employeeCode = Field(width: 155);
        var fullName = Field(width: 270);
        var job = Field(width: 190);
        var specialization = Field(width: 190);
        var qualification = Field(width: 190);
        var category = new ComboBox
        {
            Width = 175, ItemsSource = new[] { "معلم", "إداري", "أخصائي اجتماعي" },
            SelectedIndex = 0
        };
        var gender = new ComboBox
        {
            Width = 150, ItemsSource = new[] { "ذكور", "إناث", "غير محدد" },
            SelectedIndex = 2
        };
        var active = new CheckBox { Content = "الموظف على رأس العمل",
            IsChecked = true, Margin = new Thickness(6, 10, 6, 6) };

        var fields = new WrapPanel();
        foreach (var (title, control) in new (string, UIElement)[]
        {
            ("الرقم الوظيفي", employeeCode), ("الاسم الكامل", fullName),
            ("الفئة", category), ("المسمى الوظيفي", job),
            ("التخصص", specialization), ("المؤهل", qualification),
            ("الجنس", gender)
        })
        {
            var group = Stack();
            group.Children.Add(Label(title, 13)); group.Children.Add(control);
            group.Margin = new Thickness(3, 3, 3, 5);
            fields.Children.Add(group);
        }
        editor.Children.Add(fields);
        editor.Children.Add(active);
        SchoolStaff? selected = null;

        void Clear()
        {
            selected = null;
            staffGrid.SelectedItem = null;
            employeeCode.Text = ""; fullName.Text = ""; job.Text = "";
            specialization.Text = ""; qualification.Text = "";
            category.SelectedIndex = 0; gender.SelectedIndex = 2; active.IsChecked = true;
        }

        staffGrid.SelectionChanged += (_, _) =>
        {
            selected = staffGrid.SelectedItem as SchoolStaff;
            if (selected is null) return;
            employeeCode.Text = selected.EmployeeCode;
            fullName.Text = selected.FullName;
            category.SelectedItem = selected.Category;
            job.Text = selected.JobTitle;
            specialization.Text = selected.Specialization;
            qualification.Text = selected.Qualification;
            gender.SelectedItem = selected.Gender;
            active.IsChecked = selected.IsActive;
        };

        void Refresh()
        {
            Clear();
            if (schoolBox.SelectedItem is not School school) return;
            staffGrid.ItemsSource = _store.GetSchoolStaff(_actor, school.Id);
            editor.IsEnabled = AccessPolicy.CanEditStaff(_actor, school.Id);
        }

        schoolBox.SelectionChanged += (_, _) => Safe(Refresh);

        var actions = new WrapPanel();
        actions.Children.Add(Btn("سجل جديد", (_, _) => Clear()));
        actions.Children.Add(Btn("حفظ الموظف", (_, _) => Safe(() =>
        {
            if (schoolBox.SelectedItem is not School school)
                throw new InvalidOperationException("حدد المدرسة أولا.");
            var staff = new SchoolStaff(selected?.Id ?? "", school.Id, employeeCode.Text,
                fullName.Text, category.SelectedItem?.ToString() ?? "", job.Text,
                specialization.Text, qualification.Text,
                gender.SelectedItem?.ToString() ?? "غير محدد", active.IsChecked == true);
            _store.SaveSchoolStaff(_actor, staff);
            Info("تم حفظ سجل الموظف في المدرسة المحددة.");
            Refresh();
        })));
        editor.Children.Add(actions);

        var importPanel = Stack();
        importPanel.Children.Add(Label("استيراد سجلات المدارس من Excel أو Word", 16, true));
        importPanel.Children.Add(Label(
            "يدعم الجداول المنظمة .xlsx و.docx. يجب أن يحتوي كل صف على رمز المدرسة والرقم الوظيفي والاسم والفئة؛ يتم التوجيه برمز المدرسة وليس بالتخمين من اسم الملف. السجلات الموجودة بالرقم نفسه سيتم تحديثها بعد التأكيد.", 13));
        importPanel.Children.Add(Btn("اختيار ملف Excel أو Word واستيراد الكادر", async (_, _) =>
        {
            try
            {
                var file = new OpenFileDialog
                {
                    Filter = "Excel (*.xlsx)|*.xlsx|Word (*.docx)|*.docx",
                    Title = "اختر جدول سجلات المعلمين والإداريين والأخصائيين"
                };
                if (file.ShowDialog() != true) return;
                var rows = await Task.Run(() => SchoolStaffImportParser.Parse(file.FileName));
                var groups = rows.GroupBy(x => x.SchoolCode.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Key + ": " + g.Count().ToString(CultureInfo.InvariantCulture) + " سجل")
                    .Take(10);
                var message = "تم العثور على " + rows.Count + " سجلا ضمن المدارس التالية:" +
                    Environment.NewLine + string.Join(Environment.NewLine, groups) +
                    Environment.NewLine + Environment.NewLine +
                    "سيتم إضافة السجلات الجديدة وتحديث البيانات المناظرة بالرقم الوظيفي. " +
                    "أي رمز مدرسة غير صحيح سيوقف العملية كاملة. هل تعتمد الاستيراد؟";
                if (MessageBox.Show(message, "مراجعة قبل الاستيراد",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var imported = await Task.Run(() => _store.ImportStaffRecords(
                    _actor, rows, Path.GetFileName(file.FileName)));
                Info("تم تسجيل " + imported + " صفا في المدارس المطابقة دون استيراد جزئي.");
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "تعذر استيراد سجلات المدرسة",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }));
        root.Children.Add(Box(importPanel));
        root.Children.Add(Box(editor));
        Display("كادر المدارس والسجلات", root);
        schoolBox.SelectedIndex = 0;
    }

    private sealed class EnrollmentInput
    {
        public string Grade { get; set; } = "";
        public string Boys { get; set; } = "";
        public string Girls { get; set; } = "";
    }

    private static int Count(string text, string label)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
            throw new ArgumentException($"أدخل عددا صحيحا غير سالب في خانة {label}.");
        return value;
    }

    private static int[] GradesFor(School school)
    {
        if (school.Stage == "أساسي") return Enumerable.Range(1, 9).ToArray();
        if (school.Stage == "ثانوي") return Enumerable.Range(10, 3).ToArray();
        return Enumerable.Range(1, 12).ToArray();
    }

    private void ShowCensus()
    {
        var schools = _store.GetSchools(_actor).Where(s => s.IsActive).ToList();
        var root = Stack();
        if (schools.Count == 0) { root.Children.Add(Label("لا توجد مدارس نشطة متاحة لهذا الحساب.")); Display("إحصاء الطلاب والمعلمين", root); return; }
        root.Children.Add(Label("أدخل أعداد الطلاب حسب الصف والجنس، وأعداد المعلمين والفصول. اترك الصف غير المتوفر فارغا.", 14));
        var combo = new ComboBox { ItemsSource = schools, DisplayMemberPath = "Name", Width = 390,
            HorizontalAlignment = HorizontalAlignment.Left };
        var heading = new WrapPanel { Children = { Label("المدرسة:", 15, true), combo } };
        root.Children.Add(Box(heading));
        var form = Stack();
        var status = Label("", 14, true); form.Children.Add(status);
        var lines = new ObservableCollection<EnrollmentInput>();
        var table = GridFor(lines, ("الصف", "Grade", false), ("ذكور", "Boys", true), ("إناث", "Girls", true));
        table.MaxHeight = 440;
        form.Children.Add(table);
        var counts = new WrapPanel();
        var male = Field(width: 150); var female = Field(width: 150); var rooms = Field(width: 150);
        foreach (var (title, control) in new (string, UIElement)[] {
            ("المعلمون الذكور", male), ("المعلمات", female), ("الفصول الدراسية", rooms) })
        {
            var group = Stack(); group.Children.Add(Label(title, 13)); group.Children.Add(control); counts.Children.Add(group);
        }
        form.Children.Add(counts);
        form.Children.Add(Label("ملاحظات المدرسة", 14));
        var notes = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 65, MaxHeight = 125, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        form.Children.Add(notes);
        var actions = new WrapPanel();
        var editable = AccessPolicy.Has(_actor, PermissionFlags.EditCensus);
        actions.Children.Add(Btn("حفظ المسودة", (_, _) => Safe(() =>
        {
            if (combo.SelectedItem is not School selected) return;
            table.CommitEdit(DataGridEditingUnit.Cell, true);
            table.CommitEdit(DataGridEditingUnit.Row, true);
            var rows = new List<EnrollmentLine>();
            foreach (var item in lines)
            {
                if (string.IsNullOrWhiteSpace(item.Boys) && string.IsNullOrWhiteSpace(item.Girls)) continue;
                if (string.IsNullOrWhiteSpace(item.Boys) || string.IsNullOrWhiteSpace(item.Girls))
                    throw new ArgumentException($"الصف {item.Grade}: أدخل الذكور والإناث، ولو كانت القيمة صفرا.");
                rows.Add(new EnrollmentLine(item.Grade, Count(item.Boys, "ذكور الصف " + item.Grade),
                    Count(item.Girls, "إناث الصف " + item.Grade)));
            }
            var census = new CensusData(rows, Count(male.Text, "المعلمين الذكور"),
                Count(female.Text, "المعلمات"), Count(rooms.Text, "الفصول"), notes.Text);
            _store.SaveDraft(_actor, selected.Id, census);
            Info("تم حفظ مسودة الإحصاء بنجاح."); ShowCensus();
        }), editable));
        actions.Children.Add(Btn("رفع الإحصاء للمراجعة", (_, _) => Safe(() =>
        {
            if (combo.SelectedItem is not School selected) return;
            var current = _store.GetLatestSubmission(_actor, selected.Id)
                ?? throw new InvalidOperationException("يجب حفظ المسودة أولا.");
            _store.ChangeStatus(_actor, current.Id, "submit");
            Info("تم إرسال الإحصاء للمراجعة."); ShowCensus();
        }), editable));
        form.Children.Add(actions);
        root.Children.Add(Box(form));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not School school) return;
            lines.Clear();
            var latest = _store.GetLatestSubmission(_actor, school.Id);
            foreach (var grade in GradesFor(school))
            {
                var previous = latest?.Data.Rows.FirstOrDefault(x => x.Grade == grade.ToString(CultureInfo.InvariantCulture));
                lines.Add(new EnrollmentInput { Grade = grade.ToString(CultureInfo.InvariantCulture),
                    Boys = previous?.Boys.ToString(CultureInfo.InvariantCulture) ?? "",
                    Girls = previous?.Girls.ToString(CultureInfo.InvariantCulture) ?? "" });
            }
            male.Text = latest?.Data.MaleTeachers.ToString(CultureInfo.InvariantCulture) ?? "";
            female.Text = latest?.Data.FemaleTeachers.ToString(CultureInfo.InvariantCulture) ?? "";
            rooms.Text = latest?.Data.Classrooms.ToString(CultureInfo.InvariantCulture) ?? "";
            notes.Text = latest?.Data.Notes ?? "";
            status.Text = latest is null ? "لم تبدأ الاستمارة" :
                $"الحالة: {StatusName(latest.Status)} — المراجعة رقم {latest.Revision}" +
                (latest.DecisionReason is null ? "" : $" — السبب: {latest.DecisionReason}");
        };
        Display("إحصاء الطلاب والمعلمين", root);
        combo.SelectedIndex = 0;
    }

    private sealed class ReviewLine
    {
        public CensusSubmission Source { get; }
        public string SchoolName => Source.SchoolName;
        public string Year => Source.Year;
        public int Revision => Source.Revision;
        public string StatusArabic => StatusName(Source.Status);
        public int Students => Source.Data.Students;
        public int Teachers => Source.Data.Teachers;
        public ReviewLine(CensusSubmission value) { Source = value; }
    }

    private void ShowReviews()
    {
        var root = Stack();
        root.Children.Add(Label("عرض آخر مراجعة من استمارات المدارس. لا يتم اعتماد الإحصاء إلا بعد مراجعته.", 14));
        var submissions = _store.GetLatestSubmissions(_actor);
        var grid = GridFor(submissions.Select(x => new ReviewLine(x)).ToList(),
            ("المدرسة", "SchoolName", false), ("السنة", "Year", false),
            ("المراجعة", "Revision", false), ("الحالة", "StatusArabic", false),
            ("الطلاب", "Students", false), ("المعلمون", "Teachers", false));
        root.Children.Add(Box(grid));
        var actions = Stack();
        actions.Children.Add(Label("حدد استمارة، ثم اختر الإجراء المناسب لصلاحيتك:", 15, true));
        var reason = new TextBox { MinWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
        actions.Children.Add(Label("سبب الإعادة للتصحيح (إلزامي عند الإعادة)", 13));
        actions.Children.Add(reason);
        void Change(string action)
        {
            Safe(() =>
            {
                if (grid.SelectedItem is not ReviewLine chosen)
                    throw new InvalidOperationException("حدد استمارة من الجدول أولا.");
                _store.ChangeStatus(_actor, chosen.Source.Id, action, reason.Text);
                Info("تم تسجيل الإجراء بنجاح."); ShowReviews();
            });
        }
        var buttons = new WrapPanel();
        buttons.Children.Add(Btn("إقرار مدير المدرسة", (_, _) => Change("school_approve"),
            AccessPolicy.Has(_actor, PermissionFlags.ApproveSchool)));
        buttons.Children.Add(Btn("اعتماد المديرية النهائي", (_, _) => Change("district_approve"),
            AccessPolicy.Has(_actor, PermissionFlags.ApproveDistrict)));
        buttons.Children.Add(Btn("إرجاع للتصحيح", (_, _) => Change("return"),
            AccessPolicy.Has(_actor, PermissionFlags.ReviewCensus)));
        actions.Children.Add(buttons);
        root.Children.Add(Box(actions));
        Display("المراجعة والاعتماد", root);
    }

    private static string CsvCell(string? value)
    {
        var s = value ?? "";
        var check = s.TrimStart(' ', '\t', '\r', '\n');
        if (check.Length > 0 && "=+-@".Contains(check[0])) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    private void ShowReports()
    {
        if (!AccessPolicy.Has(_actor, PermissionFlags.ViewReports)) throw new UnauthorizedAccessException();
        var root = Stack();
        var approved = _store.GetLatestSubmissions(_actor).Where(x => x.Status == Statuses.Approved).ToList();
        root.Children.Add(Label($"الاستمارات المعتمدة: {approved.Count} — العام { _store.AcademicYear }", 16, true));
        var grid = GridFor(approved,
            ("المدرسة", "SchoolName", false), ("ذكور", "Data.Boys", false),
            ("إناث", "Data.Girls", false), ("طلاب", "Data.Students", false),
            ("المعلمون", "Data.Teachers", false), ("الفصول", "Data.Classrooms", false));
        root.Children.Add(Box(grid));
        root.Children.Add(Btn("تصدير Excel-compatible CSV", (_, _) => Safe(() =>
        {
            var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "SchoolObservatory-Approved.csv" };
            if (dialog.ShowDialog() != true) return;
            var rows = new List<string>
            {
                string.Join(",", new[] { "المدرسة", "السنة", "الذكور", "الإناث", "إجمالي الطلاب", "المعلمون", "الفصول" }.Select(CsvCell))
            };
            foreach (var census in approved)
                rows.Add(string.Join(",", new[] { census.SchoolName, census.Year,
                    census.Data.Boys.ToString(CultureInfo.InvariantCulture),
                    census.Data.Girls.ToString(CultureInfo.InvariantCulture),
                    census.Data.Students.ToString(CultureInfo.InvariantCulture),
                    census.Data.Teachers.ToString(CultureInfo.InvariantCulture),
                    census.Data.Classrooms.ToString(CultureInfo.InvariantCulture) }.Select(CsvCell)));
            File.WriteAllLines(dialog.FileName, rows, new UTF8Encoding(true));
            Info("تم حفظ الملف. افتحه في Excel أو أي برنامج جداول يدعم CSV UTF-8.");
        })));
        root.Children.Add(Label("ملفات PDF والقوالب الإحصائية الرسمية ستضاف بعد اعتماد نماذج الإدارة.", 13));
        Display("التقارير والتصدير", root);
    }

    private sealed record RoleOption(string Code, string Label);

    private void ShowUsers()
    {
        if (!AccessPolicy.CanCreateUsers(_actor)) throw new UnauthorizedAccessException();
        var root = Stack();
        root.Children.Add(Label("إدارة المسؤولين والمستخدمين — الصلاحيات المحددة لكل حساب", 18, true));
        root.Children.Add(Label("المسؤول المؤسس محمي ولا يمكن إيقافه أو تغيير صلاحياته. المسؤولون المفوضون لا يستطيعون إنشاء مسؤولين آخرين أو منح صلاحيات أكبر من صلاحياتهم.", 13));

        var users = _store.GetUsers(_actor);
        var grid = GridFor(users,
            ("الحساب", "Username", false), ("الدور", "Role", false),
            ("المدرسة", "SchoolId", false), ("المسؤول المؤسس", "IsOwner", false),
            ("نشط", "IsActive", false));
        root.Children.Add(Box(grid));

        var form = Stack();
        form.Children.Add(Label("إضافة حساب جديد أو تحديد حساب من الجدول لتعديل صلاحياته", 16, true));
        var username = Field(width: 280);
        var password = new PasswordBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        var confirm = new PasswordBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        var roleItems = Roles.All.Where(x => _actor.IsOwner || x != Roles.Admin)
            .OrderBy(x => RoleName(x)).Select(x => new RoleOption(x, RoleName(x))).ToArray();
        var role = new ComboBox { Width = 260, ItemsSource = roleItems,
            DisplayMemberPath = "Label", SelectedValuePath = "Code",
            HorizontalAlignment = HorizontalAlignment.Left };
        var school = new ComboBox
        {
            Width = 350, ItemsSource = _store.GetSchools(_actor).Where(x => x.IsActive).ToArray(),
            DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Left
        };

        foreach (var (title, control) in new (string, UIElement)[]
        {
            ("اسم المستخدم (3–64 حرفا أو رقما إنجليزيا)", username),
            ("كلمة المرور الجديدة (12 حرفا على الأقل وحروف وأرقام)", password),
            ("تأكيد كلمة المرور", confirm),
            ("نوع الحساب", role),
            ("المدرسة لمدير المدرسة ومدخل البيانات فقط", school)
        })
        {
            form.Children.Add(Label(title, 13));
            form.Children.Add(control);
        }

        form.Children.Add(Label("الصلاحيات التفصيلية", 16, true));
        var permissionPanel = new WrapPanel { Margin = new Thickness(0, 8, 0, 12) };
        var checks = PermissionCatalog.Choices.Select(option =>
        {
            var checkbox = new CheckBox
            {
                Content = option.Name,
                Tag = option.Flag,
                MinWidth = 260,
                Margin = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Center
            };
            permissionPanel.Children.Add(checkbox);
            return checkbox;
        }).ToArray();
        form.Children.Add(permissionPanel);

        var state = Label("الوضع: إضافة حساب جديد", 13);
        form.Children.Add(state);
        UserAccount? selected = null;

        long SelectedPermissions()
        {
            long mask = 0;
            foreach (var check in checks)
                if (check.IsChecked == true)
                    mask |= (long)(PermissionFlags)check.Tag;
            return mask;
        }

        void SetPermissions(long mask)
        {
            foreach (var check in checks)
                check.IsChecked = (mask & (long)(PermissionFlags)check.Tag) != 0;
        }

        void SetRoleDefaults()
        {
            var currentRole = role.SelectedValue?.ToString();
            if (currentRole is null) return;
            school.IsEnabled = currentRole is Roles.Principal or Roles.Entry;
            if (!school.IsEnabled) school.SelectedIndex = -1;
            SetPermissions((long)PermissionCatalog.DefaultForRole(currentRole));
            var allowed = PermissionCatalog.AllowedForRole(currentRole);
            foreach (var check in checks)
            {
                var flag = (PermissionFlags)check.Tag;
                check.IsEnabled = (allowed & flag) != 0 &&
                    (_actor.IsOwner || (_actor.PermissionMask & (long)flag) != 0);
                if (!check.IsEnabled) check.IsChecked = false;
            }
        }

        role.SelectionChanged += (_, _) => SetRoleDefaults();
        role.SelectedValue = Roles.Entry;

        grid.SelectionChanged += (_, _) =>
        {
            selected = grid.SelectedItem as UserAccount;
            if (selected is null) return;
            username.Text = selected.Username;
            username.IsReadOnly = true;
            role.SelectedValue = selected.Role;
            role.IsEnabled = false;
            school.SelectedItem = _store.GetSchools(_actor).FirstOrDefault(x => x.Id == selected.SchoolId);
            school.IsEnabled = false;
            SetPermissions(selected.PermissionMask);
            var canManage = AccessPolicy.CanManageAccount(_actor, selected);
            foreach (var check in checks) check.IsEnabled &= canManage;
            password.Clear();
            confirm.Clear();
            state.Text = !canManage
                ? "هذا الحساب محمي أو أعلى من صلاحياتك، ولا يمكنك إدارته."
                : $"تعديل الحساب: {selected.Username} — التغييرات في الصلاحيات تتطلب تسجيل دخوله مجددا.";
        };

        void NewUser()
        {
            grid.SelectedItem = null;
            selected = null;
            username.IsReadOnly = false;
            username.Clear();
            password.Clear();
            confirm.Clear();
            role.IsEnabled = true;
            role.SelectedValue = Roles.Entry;
            school.SelectedItem = null;
            SetRoleDefaults();
            state.Text = "الوضع: إضافة حساب جديد";
        }

        var buttons = new WrapPanel();
        buttons.Children.Add(Btn("حساب جديد", (_, _) => NewUser()));
        buttons.Children.Add(Btn("إنشاء الحساب", (_, _) => Safe(() =>
        {
            if (selected is not null)
                throw new InvalidOperationException("اختر حساب جديد قبل الإضافة.");
            if (password.Password != confirm.Password)
                throw new ArgumentException("كلمتا المرور غير متطابقتين.");
            _store.CreateUser(_actor, username.Text, password.Password,
                role.SelectedValue?.ToString() ?? "", (school.SelectedItem as School)?.Id,
                SelectedPermissions());
            Info("تم إنشاء الحساب بصلاحياته المحددة."); ShowUsers();
        })));
        buttons.Children.Add(Btn("حفظ الصلاحيات المحددة", (_, _) => Safe(() =>
        {
            if (selected is null) throw new InvalidOperationException("حدد حسابا من الجدول.");
            _store.UpdateUserPermissions(_actor, selected.Id, SelectedPermissions());
            Info("تم تحديث الصلاحيات. يجب على المستخدم تسجيل الدخول مجددا."); ShowUsers();
        })));
        buttons.Children.Add(Btn("إيقاف أو تفعيل الحساب", (_, _) => Safe(() =>
        {
            if (selected is null) throw new InvalidOperationException("حدد حسابا من الجدول.");
            var activate = !selected.IsActive;
            var answer = MessageBox.Show(
                activate ? "هل تريد تفعيل الحساب المحدد؟" : "هل تريد إيقاف حساب المستخدم المحدد؟",
                "تأكيد إدارة الحساب", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _store.SetUserActive(_actor, selected.Id, activate);
            Info(activate ? "تم تفعيل الحساب." : "تم إيقاف الحساب."); ShowUsers();
        })));
        buttons.Children.Add(Btn("إعادة تعيين كلمة المرور", (_, _) => Safe(() =>
        {
            if (selected is null) throw new InvalidOperationException("حدد حسابا من الجدول.");
            if (password.Password != confirm.Password)
                throw new ArgumentException("كلمتا المرور غير متطابقتين.");
            _store.ResetUserPassword(_actor, selected.Id, password.Password);
            password.Clear(); confirm.Clear();
            Info("تم تعيين كلمة المرور الجديدة بنجاح. أبلغ صاحب الحساب بطريقة آمنة.");
        })));
        form.Children.Add(buttons);
        root.Children.Add(Box(form));
        Display("إدارة المسؤولين والمستخدمين", root);
    }

    private void ShowAudit()
    {
        if (!AccessPolicy.Has(_actor, PermissionFlags.ViewAudit)) throw new UnauthorizedAccessException();
        var root = Stack();
        root.Children.Add(Label("آخر 100 إجراء مسجل. لا تسمح الواجهة بحذف سجل العمليات.", 14));
        root.Children.Add(Box(GridFor(_store.GetRecentAudit(_actor),
            ("التوقيت", "When", false), ("المستخدم", "Actor", false),
            ("الإجراء", "Action", false), ("الكيان", "EntityId", false), ("التفاصيل", "Details", false))));
        Display("سجل العمليات", root);
    }
}
