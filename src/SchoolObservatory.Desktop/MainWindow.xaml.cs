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
    private readonly SqliteStore _store;
    private readonly UserAccount _actor;
    private readonly Brush _primary = new SolidColorBrush(Color.FromRgb(21, 101, 192));
    private readonly Brush _neutral = new SolidColorBrush(Color.FromRgb(236, 243, 251));
    private readonly Brush _muted = new SolidColorBrush(Color.FromRgb(89, 111, 139));

    public MainWindow(SqliteStore store, UserAccount actor)
    {
        InitializeComponent();
        _store = store;
        _actor = actor;
        AccountText.Text = $"{actor.Username}  |  {RoleName(actor.Role)}";
        YearText.Text = $"العام الدراسي: {store.AcademicYear}";
        AddNav("لوحة التحكم", ShowDashboard);
        AddNav("دليل المدارس", ShowSchools);
        AddNav("إحصاء الطلاب والمعلمين", ShowCensus);
        AddNav("مراجعة واعتماد الإحصاء", ShowReviews);
        AddNav("التقارير والتصدير", ShowReports);
        if (AccessPolicy.CanCreateUsers(actor)) AddNav("إدارة المستخدمين", ShowUsers);
        if (actor.Role is Roles.Admin or Roles.Statistician or Roles.Reviewer) AddNav("سجل العمليات", ShowAudit);
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
            Content = text, Background = new SolidColorBrush(Color.FromRgb(30, 77, 126)),
            BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(2, 3, 2, 3)
        };
        button.Click += (_, _) => action();
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
    private void Logout_Click(object sender, RoutedEventArgs e) => Close();

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
            ("الملكية", "Ownership", false), ("المرحلة", "Stage", false), ("نشطة", "IsActive", false));
        root.Children.Add(Box(grid));
        if (AccessPolicy.CanEditSchool(_actor))
        {
            var form = Stack();
            form.Children.Add(Label("إضافة مدرسة أو تعديل المدرسة المحددة", 17, true));
            var code = Field(width: 150); var name = Field(width: 300); var zone = Field(width: 210);
            var ownership = new ComboBox { Width = 160, ItemsSource = new[] { "حكومية", "أهلية" }, SelectedIndex = 0 };
            var stage = new ComboBox { Width = 160, ItemsSource = new[] { "أساسي", "ثانوي", "أساسي وثانوي" }, SelectedIndex = 0 };
            var active = new CheckBox { Content = "المدرسة نشطة", IsChecked = true, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
            var fields = new WrapPanel();
            foreach (var (title, control) in new (string, UIElement)[] {
                ("رمز المدرسة",code),("اسم المدرسة",name),("المنطقة",zone),
                ("الملكية",ownership),("المرحلة",stage) })
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
                active.IsChecked = selected.IsActive;
            };
            var buttons = new WrapPanel();
            buttons.Children.Add(Btn("إضافة مدرسة جديدة", (_, _) => Safe(() =>
            {
                _store.AddSchool(_actor, code.Text, name.Text, zone.Text, ownership.SelectedItem?.ToString() ?? "", stage.SelectedItem?.ToString() ?? "");
                Info("تمت إضافة المدرسة."); ShowSchools();
            })));
            buttons.Children.Add(Btn("حفظ تعديل المدرسة المحددة", (_, _) => Safe(() =>
            {
                if (selected is null) throw new InvalidOperationException("حدد المدرسة المطلوب تعديلها من الجدول.");
                _store.UpdateSchool(_actor, selected with
                {
                    Code = code.Text, Name = name.Text, Zone = zone.Text,
                    Ownership = ownership.SelectedItem?.ToString() ?? "",
                    Stage = stage.SelectedItem?.ToString() ?? "", IsActive = active.IsChecked == true
                });
                Info("تم حفظ بيانات المدرسة."); ShowSchools();
            })));
            buttons.Children.Add(Btn("استيراد مدارس من Excel CSV", (_, _) => Safe(() =>
            {
                var file = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv", Title = "اختر قائمة المدارس بصيغة CSV UTF-8" };
                if (file.ShowDialog() != true) return;
                if (new FileInfo(file.FileName).Length > 5_000_000)
                    throw new ArgumentException("حجم الملف يتجاوز 5 ميجابايت.");
                var entries = SchoolCsvParser.Parse(File.ReadAllText(file.FileName, Encoding.UTF8));
                if (MessageBox.Show($"سيتم إضافة {entries.Count} مدرسة. هل تريد المتابعة؟",
                    "تأكيد الاستيراد", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var imported = _store.ImportSchools(_actor, entries);
                Info($"تم استيراد {imported} مدرسة دون أخطاء."); ShowSchools();
            })));
            buttons.Children.Add(Btn("مسح الحقول", (_, _) =>
            {
                selected = null; grid.SelectedItem = null; code.Text = ""; name.Text = "";
                zone.Text = ""; active.IsChecked = true;
            }));
            form.Children.Add(buttons);
            root.Children.Add(Box(form));
        }
        Display("دليل المدارس", root);
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
        var editable = _actor.Role is Roles.Admin or Roles.Statistician or Roles.Principal or Roles.Entry;
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
        buttons.Children.Add(Btn("إقرار مدير المدرسة", (_, _) => Change("school_approve")));
        buttons.Children.Add(Btn("اعتماد المديرية النهائي", (_, _) => Change("district_approve")));
        buttons.Children.Add(Btn("إرجاع للتصحيح", (_, _) => Change("return")));
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

    private void ShowUsers()
    {
        if (!AccessPolicy.CanCreateUsers(_actor)) throw new UnauthorizedAccessException();
        var root = Stack();
        var users = _store.GetUsers(_actor);
        root.Children.Add(Box(GridFor(users, ("اسم المستخدم", "Username", false),
            ("الصلاحية", "Role", false), ("المدرسة (معرف)", "SchoolId", false), ("نشط", "IsActive", false))));
        var form = Stack();
        form.Children.Add(Label("إضافة مستخدم ومنحه صلاحية محددة", 16, true));
        var username = Field(width: 250);
        var password = new PasswordBox { Width = 250, HorizontalAlignment = HorizontalAlignment.Left };
        var role = new ComboBox { Width = 250, ItemsSource = Roles.All.OrderBy(x => x).ToArray(), SelectedItem = Roles.Entry };
        var school = new ComboBox { Width = 360, ItemsSource = _store.GetSchools(_actor).Where(x => x.IsActive).ToArray(),
            DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (title, control) in new (string, UIElement)[] {
            ("اسم المستخدم بالإنجليزية",username),("كلمة المرور",password),("الدور",role),
            ("المدرسة (مطلوبة لمدير المدرسة ومدخل البيانات فقط)",school) })
        {
            form.Children.Add(Label(title, 13)); form.Children.Add(control);
        }
        form.Children.Add(Btn("حفظ المستخدم", (_, _) => Safe(() =>
        {
            _store.CreateUser(_actor, username.Text, password.Password,
                role.SelectedItem?.ToString() ?? "", (school.SelectedItem as School)?.Id);
            Info("تم إنشاء المستخدم."); ShowUsers();
        })));
        root.Children.Add(Box(form));
        Display("إدارة المستخدمين", root);
    }

    private void ShowAudit()
    {
        var root = Stack();
        root.Children.Add(Label("آخر 100 إجراء مسجل. لا تسمح الواجهة بحذف سجل العمليات.", 14));
        root.Children.Add(Box(GridFor(_store.GetRecentAudit(_actor),
            ("التوقيت", "When", false), ("المستخدم", "Actor", false),
            ("الإجراء", "Action", false), ("الكيان", "EntityId", false), ("التفاصيل", "Details", false))));
        Display("سجل العمليات", root);
    }
}
