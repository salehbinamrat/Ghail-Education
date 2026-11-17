using System.Collections.ObjectModel;
using System.Windows.Automation;
using SchoolObservatory.Desktop.Services;
using SchoolObservatory.Desktop.ViewModels;
using SchoolObservatory.Desktop.Views;
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
    private readonly NavigationContext _navigationContext = new();
    private bool _sidebarUserOverride;
    private bool _sidebarExpanded = true;
    private bool _lastCompact;
    private readonly Brush _primary = new SolidColorBrush(Color.FromRgb(0, 148, 177));
    private readonly Brush _neutral = new SolidColorBrush(Color.FromRgb(236, 243, 251));
    private readonly Brush _muted = new SolidColorBrush(Color.FromRgb(89, 111, 139));

    public MainWindow(SqliteStore store, UserAccount actor)
    {
        InitializeComponent();
        Loaded += (_, _) => { WindowLayout.FitToWorkArea(this); ApplySidebarLayout(); };
        SizeChanged += (_, _) => ApplySidebarLayout();
        _store = store;
        _actor = actor;
        AccountText.Text = $"{actor.Username}  |  {(actor.IsOwner ? "المسؤول المؤسس" : RoleName(actor.Role))}";
        YearText.Text = $"العام الدراسي: {store.AcademicYear}";
        AddNav("لوحة التحكم", ShowDashboard);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewSchools))
            AddNav("الأعوام الدراسية والأرشيف", ShowAcademicYears);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewSchools))
            AddNav("دليل المدارس", ShowSchools);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewStaff))
            AddNav("كادر المدارس والسجلات", ShowStaff);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewStudents))
        {
            AddNav("الطلاب والصفوف", ShowStudents);
            AddNav("أرشيف الطلاب والترفيع", ShowHistoricalRosters);
        }
        if (AccessPolicy.Has(actor, PermissionFlags.EditCensus) || AccessPolicy.Has(actor, PermissionFlags.ViewReports)) AddNav("إحصاء الطلاب والمعلمين", ShowCensus);
        if (AccessPolicy.Has(actor, PermissionFlags.ApproveSchool) || AccessPolicy.Has(actor, PermissionFlags.ApproveDistrict) || AccessPolicy.Has(actor, PermissionFlags.ReviewCensus)) AddNav("مراجعة واعتماد الإحصاء", ShowReviews);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewReports)) AddNav("التقارير والتصدير", ShowReports);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewSchools) &&
            (AccessPolicy.CanEditSchool(actor) ||
             AccessPolicy.Has(actor, PermissionFlags.ManageStaff) ||
             AccessPolicy.Has(actor, PermissionFlags.ManageStudents)))
            AddNav("الاستيراد الآمن وسجل الدفعات", ShowImportHistory);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewReports) &&
            AccessPolicy.Has(actor, PermissionFlags.ViewStudents) &&
            AccessPolicy.Has(actor, PermissionFlags.ViewStaff) &&
            AccessPolicy.Has(actor, PermissionFlags.ViewSchools))
            AddNav("مطابقة السجلات والإحصاء", ShowReconciliation);
        if (AccessPolicy.CanCreateUsers(actor)) AddNav("إدارة المستخدمين", ShowUsers);
        if (AccessPolicy.Has(actor, PermissionFlags.ViewAudit)) AddNav("سجل العمليات", ShowAudit);
        if (actor.IsOwner) AddNav("النسخ الاحتياطي والاستعادة", ShowBackups);
        StartAutomaticBackup();
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

    private readonly Dictionary<Button, Brush> _navBaseColors = new();

    private void AddNav(string text, Action action)
    {
        var menuColor = text switch
        {
            "لوحة التحكم" => Color.FromRgb(10, 88, 150),
            "الطلاب والصفوف" => Color.FromRgb(4, 108, 123),
            "الأعوام الدراسية والأرشيف" => Color.FromRgb(31, 99, 122),
            "إدارة المستخدمين" => Color.FromRgb(79, 67, 143),
            "التقارير والتصدير" => Color.FromRgb(3, 99, 137),
            "سجل العمليات" => Color.FromRgb(48, 86, 116),
            _ => Color.FromRgb(7, 88, 130)
        };
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("NavigationButton"),
            Content = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Arial"), FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, TextAlignment = TextAlignment.Right
            },
            Tag = text,
            Background = new SolidColorBrush(menuColor),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(87, 153, 183)),
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(2, 4, 2, 4),
            MinHeight = 48,
            ToolTip = text
        };
        _navBaseColors[button] = button.Background;
        button.Click += (_, _) =>
        {
            Safe(action);
            if (ResponsiveSizing.ShouldCollapseSidebar(ActualWidth))
            {
                _sidebarUserOverride = true;
                _sidebarExpanded = false;
                ApplySidebarLayout();
            }
        };
        NavPanel.Children.Add(button);
    }

    private void UpdateActiveNavigation(string title)
    {
        var key = title switch
        {
            "ملف الطالب" => "الطلاب والصفوف",
            "ملف المدرسة" => "دليل المدارس",
            "ملف الموظف" => "كادر المدارس والسجلات",
            "المراجعة والاعتماد" => "مراجعة واعتماد الإحصاء",
            "إدارة المسؤولين والمستخدمين" => "إدارة المستخدمين",
            _ => title
        };
        foreach (var entry in _navBaseColors)
        {
            var selected = string.Equals(entry.Key.Tag as string, key,
                StringComparison.Ordinal);
            entry.Key.Background = selected
                ? new SolidColorBrush(Color.FromRgb(21, 94, 203))
                : entry.Value;
            entry.Key.BorderBrush = selected
                ? new SolidColorBrush(Color.FromRgb(255, 208, 106))
                : new SolidColorBrush(Color.FromRgb(87, 153, 183));
            entry.Key.BorderThickness = selected
                ? new Thickness(4, 1, 1, 1)
                : new Thickness(1);
            entry.Key.FontWeight = FontWeights.Bold;
            entry.Key.Opacity = selected ? 1 : 0.93;
        }
    }


    private void NavigationToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarUserOverride = true;
        _sidebarExpanded = SidebarPanel.Visibility != Visibility.Visible;
        ApplySidebarLayout();
    }

    private void ApplySidebarLayout()
    {
        if (!IsLoaded || ActualWidth <= 0) return;
        var compact = ResponsiveSizing.ShouldCollapseSidebar(ActualWidth);
        if (_lastCompact != compact)
        {
            _sidebarUserOverride = false;
            _lastCompact = compact;
        }
        var visible = _sidebarUserOverride ? _sidebarExpanded : !compact;
        SidebarPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(visible
            ? compact ? Math.Clamp(ActualWidth * 0.29, 145, 205)
                      : ResponsiveSizing.SidebarWidth(ActualWidth)
            : 0);
        NavigationToggle.Content = visible ? "☰ إخفاء القائمة" : "☰ إظهار القائمة";
    }

    private void Display(string title, UIElement body)
    {
        if (!string.Equals(SectionTitle.Text, title, StringComparison.Ordinal))
        {
            StatusMessage.Text = "";
            StatusMessage.Visibility = Visibility.Collapsed;
        }
        SectionTitle.Text = title;
        ContentHost.Content = body;
        // Opening a different page or profile should start at its heading, not at
        // the vertical position left from the previous long form.
        ContentScroll.ScrollToTop();
        UpdateActiveNavigation(title);
    }

    private static StackPanel Stack(int margin = 0) => new()
    {
        Orientation = Orientation.Vertical,
        Margin = new Thickness(margin)
    };

    private static TextBlock Label(string text, int size = 16, bool bold = false) => new()
    {
        Text = text, FontSize = size,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(3, 6, 3, 6), TextWrapping = TextWrapping.Wrap
    };

    private static TextBox Field(string? value = null, double width = 200) => new()
    {
        Text = value ?? "", MinWidth = width, Width = width, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center, MaxLength = 180, TextAlignment = TextAlignment.Right
    };

    private static Button Btn(string title, RoutedEventHandler click, bool allowed = true,
        string tone = "primary")
    {
        // Distinguish save, approval, warning and destructive actions throughout the program.
        var effectiveTone = tone != "primary" ? tone :
            title.Contains("إيقاف", StringComparison.Ordinal) ||
            title.Contains("إعادة تعيين", StringComparison.Ordinal) ? "danger" :
            title.Contains("إرجاع", StringComparison.Ordinal) ||
            title.Contains("رفع الإحصاء", StringComparison.Ordinal) ? "warning" :
            title.Contains("اعتماد", StringComparison.Ordinal) ||
            title.Contains("إقرار", StringComparison.Ordinal) ? "purple" :
            title.Contains("حفظ", StringComparison.Ordinal) ||
            title.Contains("إضافة", StringComparison.Ordinal) ||
            title.Contains("إنشاء", StringComparison.Ordinal) ? "success" :
            title.Contains("تصدير", StringComparison.Ordinal) ||
            title.Contains("حساب جديد", StringComparison.Ordinal) ? "secondary" : "primary";
        var (background, foreground) = effectiveTone switch
        {
            "success" => (Color.FromRgb(0, 132, 122), Colors.White),
            "secondary" => (Color.FromRgb(219, 241, 250), Color.FromRgb(12, 67, 102)),
            "warning" => (Color.FromRgb(215, 128, 27), Colors.White),
            "danger" => (Color.FromRgb(174, 43, 65), Colors.White),
            "purple" => (Color.FromRgb(104, 81, 174), Colors.White),
            _ => (Color.FromRgb(0, 130, 179), Colors.White)
        };
        var btn = new Button
        {
            Content = title, IsEnabled = allowed, Margin = new Thickness(4, 7, 4, 7),
            Background = new SolidColorBrush(background),
            Foreground = new SolidColorBrush(foreground),
            BorderBrush = new SolidColorBrush(Color.FromRgb(7, 91, 126)),
            MinHeight = 42
        };
        AutomationProperties.SetName(btn, title);
        btn.Click += click;
        return btn;
    }

    private static Border Box(UIElement child, Thickness? padding = null) => new()
    {
        Child = child, Padding = padding ?? new Thickness(17), Background = Brushes.White,
        CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 16),
        BorderBrush = new SolidColorBrush(Color.FromRgb(184, 223, 233)), BorderThickness = new Thickness(1)
    };

    // Long Arabic school and personnel names must not be squeezed to equal-width narrow columns.
    // Retain horizontal scrolling, sorting, resizing and UI virtualization on every table.
    private static DataGrid GridFor<T>(IEnumerable<T> data, params (string Header, string Property, bool Editable)[] cols)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, ItemsSource = data, MinHeight = 160, MaxHeight = 520,
            IsReadOnly = cols.All(x => !x.Editable), SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            CanUserResizeColumns = true, CanUserSortColumns = true,
            EnableRowVirtualization = true, EnableColumnVirtualization = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Star),
            HeadersVisibility = DataGridHeadersVisibility.All,
            RowHeaderWidth = 56
        };
        // A virtualized row header provides sequential 1-based numbering without
        // modifying school identifiers, employee codes or imported student codes.
        grid.LoadingRow += (_, e) =>
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.InvariantCulture);
            e.Row.ToolTip = "رقم تسلسلي تلقائي في الكشف";
        };
        var cellText = new Style(typeof(TextBlock));
        cellText.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        cellText.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
        cellText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        cellText.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        cellText.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 2, 8, 2)));
        foreach (var col in cols)
        {
            var minWidth = col.Property switch
            {
                "Name" or "FullName" or "SchoolName" => 225,
                "JobTitle" or "Specialization" or "Qualification" => 175,
                "Username" or "SchoolDisplayName" or "RoleDisplayName" => 160,
                "Zone" or "Category" or "StatusArabic" => 145,
                "Code" or "EmployeeCode" or "Year" => 120,
                "Grade" or "Boys" or "Girls" => 90,
                _ => 115
            };
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = col.Header, IsReadOnly = !col.Editable,
                MinWidth = minWidth, MaxWidth = 420,
                ElementStyle = cellText,
                Binding = new Binding(col.Property) { Mode = col.Editable ? BindingMode.TwoWay : BindingMode.OneWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
            });
        }
        return grid;
    }


    /// <summary>Text is visible and announced even when the grid has no matching records.</summary>
    private static Grid TableSurface(DataGrid grid, string emptyMessage = "لا توجد سجلات تطابق الاختيارات الحالية.")
    {
        var surface = new Grid();
        surface.Children.Add(grid);
        var note = new TextBlock
        {
            Text = emptyMessage, FontFamily = new FontFamily("Arial"),
            FontWeight = FontWeights.Bold,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
            Style = (Style)Application.Current.FindResource("ProfessionalEmptyState")
        };
        AutomationProperties.SetName(note, emptyMessage);
        void UpdateEmpty() =>
            note.Visibility = grid.HasItems ? Visibility.Collapsed : Visibility.Visible;
        grid.Loaded += (_, _) => UpdateEmpty();
        ((System.Collections.Specialized.INotifyCollectionChanged)grid.Items)
            .CollectionChanged += (_, _) => UpdateEmpty();
        surface.Children.Add(note);
        UpdateEmpty();
        return surface;
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusMessage.Text = message;
        StatusMessage.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(161, 30, 53) : Color.FromRgb(6, 93, 103));
        StatusMessage.Visibility = Visibility.Visible;
    }

    private void Info(string text) => ShowStatus(text);
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        LogoutRequested = true;
        Close();
    }

    /// <summary>Phase 5 incrementally moves the dashboard to an async View/ViewModel/service.</summary>
    private void ShowDashboard()
    {
        var model = new DashboardViewModel(new DashboardService(_store, _actor));
        var view = new DashboardView { DataContext = model };
        view.RefreshRequested += async (_, _) => await model.LoadAsync();
        view.ReportsRequested += (_, _) => Safe(ShowReports);
        view.ReconciliationRequested += (_, _) => Safe(ShowReconciliation);
        Display("لوحة التحكم", view);
        _ = model.LoadAsync();
    }

    // Ignore headers, scrollbars, empty backgrounds and other non-row double-clicks.
    private static bool IsDoubleClickOnGridRow(object original, DataGrid grid)
    {
        var current = original as DependencyObject;
        while (current is not null && current != grid && current is not DataGridRow)
            current = VisualTreeHelper.GetParent(current);
        return current is DataGridRow;
    }

    private void ShowSchools()
    {
        var root = Stack();
        var data = _store.GetSchools(_actor);
        root.Children.Add(Label($"عدد المدارس الظاهرة: {data.Count} — حسب الصلاحيات المحددة لحسابك", 15));
        School? selected = null;
        var schoolSearch = Field(_navigationContext.Get("schools.search"), width: 335);
        schoolSearch.ToolTip = "ابحث برمز المدرسة أو اسمها أو المنطقة أو المرحلة";
        root.Children.Add(Box(LineOfFields(("بحث سريع في دليل المدارس", schoolSearch))));
        var grid = GridFor(data,
            ("الرمز", "Code", false), ("المدرسة", "Name", false), ("المنطقة", "Zone", false),
            ("الملكية", "Ownership", false), ("النوع", "Gender", false),
            ("المرحلة", "Stage", false), ("نشطة", "IsActive", false));
        root.Children.Add(Box(TableSurface(grid, "لم يتم العثور على مدارس مطابقة. غيّر عبارة البحث.")));
        grid.ItemsSource = DirectorySearch.FindSchools(data, schoolSearch.Text);
        schoolSearch.TextChanged += (_, _) =>
        {
            selected = null;
            grid.SelectedItem = null;
            _navigationContext.Set("schools.search", schoolSearch.Text);
            grid.ItemsSource = DirectorySearch.FindSchools(data, schoolSearch.Text);
        };
        root.Children.Add(Label("انقر مرتين على اسم المدرسة لفتح ملفها الكامل، أو حددها ثم اضغط زر فتح الملف.", 13));
        grid.MouseDoubleClick += (_, e) =>
        {
            if (IsDoubleClickOnGridRow(e.OriginalSource, grid) &&
                grid.SelectedItem is School selectedSchool)
                Safe(() => ShowSchoolProfile(selectedSchool.Id));
        };
        root.Children.Add(Btn("فتح ملف المدرسة المحددة", (_, _) => Safe(() =>
        {
            if (grid.SelectedItem is not School selectedSchool)
                throw new InvalidOperationException("اختر مدرسة من الدليل أولا.");
            ShowSchoolProfile(selectedSchool.Id);
        }), tone: "secondary"));
        if (AccessPolicy.CanEditSchool(_actor))
        {
            var form = Stack();
            form.Children.Add(Label("إضافة مدرسة أو تصحيح بيانات مدرسة: اختر صفها من الجدول ثم عدل الحقول واحفظ.", 17, true));
            var code = Field(width: 150); var name = Field(width: 300); var zone = Field(width: 210);
            var ownership = new ComboBox { Width = 160, ItemsSource = new[] { "حكومية", "أهلية" }, SelectedIndex = 0 };
            var stage = new ComboBox { Width = 160, ItemsSource = new[] { "روضة", "أساسي", "ثانوي", "مجمع تربوي" }, SelectedIndex = 0 };
            var gender = new ComboBox { Width = 160, ItemsSource = new[] { "ذكور", "إناث", "مختلطة" }, SelectedIndex = 2 };
            var active = new CheckBox { Content = "المدرسة نشطة", IsChecked = true, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
            var fields = new WrapPanel();
            foreach (var (title, control) in new (string, UIElement)[] {
                ("رمز المدرسة",code),("اسم المدرسة",name),("المنطقة",zone),
                ("الملكية",ownership),("نوع المدرسة",gender),("المرحلة",stage) })
            {
                var group = Stack(); group.Margin = new Thickness(4, 4, 10, 10);
                group.Children.Add(Label(title, 13)); group.Children.Add(control); fields.Children.Add(group);
            }
            form.Children.Add(fields);
            form.Children.Add(active);
            var validation = new InlineValidationPresenter();
            form.Children.Add(validation.Message);
            var editors = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase)
            {
                ["code"] = code, ["name"] = name, ["zone"] = zone,
                ["ownership"] = ownership, ["stage"] = stage, ["gender"] = gender
            };
            bool ValidateSchoolForm() =>
                validation.Show(SchoolFormValidator.Validate(code.Text, name.Text, zone.Text,
                    ownership.SelectedItem?.ToString(), stage.SelectedItem?.ToString(),
                    gender.SelectedItem?.ToString()), editors);
            grid.SelectionChanged += (_, _) =>
            {
                selected = grid.SelectedItem as School;
                if (selected is null) return;
                _navigationContext.Set("schools.selected", selected.Id);
                validation.Clear();
                code.Text = selected.Code; name.Text = selected.Name; zone.Text = selected.Zone;
                ownership.SelectedItem = selected.Ownership; stage.SelectedItem = selected.Stage;
                gender.SelectedItem = selected.Gender;
                active.IsChecked = selected.IsActive;
            };
            var buttons = new WrapPanel();
            buttons.Children.Add(Btn("إضافة مدرسة جديدة", (_, _) =>
            {
                if (!ValidateSchoolForm()) return;
                try
                {
                    _store.AddSchool(_actor, code.Text, name.Text, zone.Text,
                        ownership.SelectedItem?.ToString() ?? "",
                        stage.SelectedItem?.ToString() ?? "",
                        gender.SelectedItem?.ToString() ?? "مختلطة");
                    Info("تمت إضافة المدرسة."); ShowSchools();
                }
                catch (Exception ex) { validation.ShowError(ex.Message); }
            }));
            buttons.Children.Add(Btn("حفظ تعديل المدرسة المحددة", (_, _) =>
            {
                if (selected is null)
                {
                    validation.ShowError("حدد المدرسة المطلوب تعديلها من الجدول.");
                    return;
                }
                if (!ValidateSchoolForm()) return;
                try
                {
                    _store.UpdateSchool(_actor, selected with
                    {
                        Code = code.Text, Name = name.Text, Zone = zone.Text,
                        Ownership = ownership.SelectedItem?.ToString() ?? "",
                        Stage = stage.SelectedItem?.ToString() ?? "",
                        Gender = gender.SelectedItem?.ToString() ?? "مختلطة",
                        IsActive = active.IsChecked == true
                    });
                    Info("تم حفظ بيانات المدرسة."); ShowSchools();
                }
                catch (Exception ex) { validation.ShowError(ex.Message); }
            }));
            if (_actor.IsOwner)
                buttons.Children.Add(Btn("حذف نهائي لمدرسة مؤرشفة وخالية فقط",(_,_)=>Safe(()=>
                {
                    if(selected is null)
                        throw new InvalidOperationException("حدد مدرسة من الجدول أولا.");
                    if(selected.IsActive)
                        throw new InvalidOperationException("أرشف المدرسة واحفظها قبل الحذف النهائي.");
                    if(MessageBox.Show(
                        $"طلب حذف المدرسة المؤرشفة: {selected.Name}؟\\n" +
                        "لن يسمح بالحذف إذا وُجد أي سجل دراسي أو وظيفي أو إحصائي أو سجل استيراد مرتبط بها. " +
                        "للتأكيد يجب أن يطابق رمز المدرسة في حقل الرمز بياناتها الأصلية.",
                        "حذف نهائي — للمسؤول المؤسس فقط",MessageBoxButton.YesNo,
                        MessageBoxImage.Stop)!=MessageBoxResult.Yes)return;
                    _store.DeleteEmptySchool(_actor,selected.Id,code.Text.Trim());
                    Info("حُذفت المدرسة الخالية بعد فحص جميع المراجع.");
                    ShowSchools();
                }),tone:"danger"));
            buttons.Children.Add(Btn("معالج استيراد المدارس (CSV / Excel / Word)",
                (_,_) => ShowImportWizard(ImportEntity.Schools),tone:"secondary"));
            buttons.Children.Add(Btn("مسح الحقول", (_, _) =>
            {
                selected = null; grid.SelectedItem = null; _navigationContext.Clear("schools.selected");
                validation.Clear(); code.Text = ""; name.Text = "";
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
                ItemsSource = new[] { "روضة", "أساسي", "ثانوي", "مجمع تربوي" },
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
        // Preserve the chosen record when navigating to a profile and back.
        var selectedId = _navigationContext.Get("schools.selected");
        if (selectedId.Length > 0)
            grid.SelectedItem = (grid.ItemsSource as IEnumerable<School>)?
                .FirstOrDefault(x => x.Id == selectedId);
        Display("دليل المدارس", root);
    }

    private void ShowStaff() => ShowStaffCore();

    private void ShowStaffCore(string? preferredSchoolId = null)
    {
        if (!AccessPolicy.Has(_actor, PermissionFlags.ViewStaff))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض كادر المدارس.");
        var schools = _store.GetSchools(_actor).Where(s => s.IsActive).ToList();
        var root = Stack();
        root.Children.Add(Label("سجلات المعلمين والطاقم الإداري والأخصائيين الاجتماعيين", 18, true));
        root.Children.Add(Label("كل حساب مدرسة يرى سجلات مدرسته فقط. أما مسؤولو المديرية فيرون المدارس وفقا لصلاحياتهم.", 13));
        // School and district personnel lists are live views of the same school_staff IDs.
        // Never store a separate duplicate district roster.
        var districtPeople = _store.GetDistrictStaffDirectory(_actor);
        var unified = Stack();
        unified.Children.Add(Label("دليل الكادر الوظيفي الموحد — جميع المدارس المصرح لك بها", 18, true));
        unified.Children.Add(Label("المدرسة التي يتبعها الموظف مستقلة عن جهة عمله الفعلية؛ انقر مرتين لفتح ملفه.", 14));
        var districtSearch = Field(_navigationContext.Get("staff.search"), width: 350);
        districtSearch.ToolTip = "ابحث بالاسم أو الرقم أو المدرسة أو الجهة المنتدب إليها";
        unified.Children.Add(LineOfFields(("بحث الكادر على مستوى المديرية", districtSearch)));
        var districtGrid = GridFor(districtPeople,
            ("الرقم الوظيفي", "EmployeeCode", false),
            ("الاسم", "FullName", false),
            ("المدرسة المسجل بها", "SchoolName", false),
            ("الفئة", "Category", false),
            ("المسمى الوظيفي", "JobTitle", false),
            ("الصفة الوظيفية", "EmploymentType", false),
            ("وضع العمل", "AssignmentStatus", false),
            ("جهة العمل الفعلية", "EffectiveWorkplace", false),
            ("القيد", "ActiveLabel", false));
        districtGrid.MaxHeight = 370;
        districtGrid.ItemsSource = DirectorySearch.FindDistrictStaff(
            districtPeople, districtSearch.Text);
        districtSearch.TextChanged += (_, _) =>
        {
            _navigationContext.Set("staff.search", districtSearch.Text);
            districtGrid.ItemsSource = DirectorySearch.FindDistrictStaff(
                districtPeople, districtSearch.Text);
        };
        districtGrid.MouseDoubleClick += (_, e) =>
        {
            if (IsDoubleClickOnGridRow(e.OriginalSource, districtGrid) &&
                districtGrid.SelectedItem is DistrictStaffDirectoryRow selectedPerson)
                Safe(() => ShowStaffProfile(selectedPerson.SchoolId, selectedPerson.StaffId));
        };
        unified.Children.Add(TableSurface(districtGrid,
            "لا يوجد موظفون يطابقون البحث في الدليل الموحد."));
        unified.Children.Add(Btn("فتح الملف الوظيفي المحدد", (_, _) => Safe(() =>
        {
            if (districtGrid.SelectedItem is not DistrictStaffDirectoryRow selectedPerson)
                throw new InvalidOperationException("اختر موظفا من دليل المديرية أولا.");
            ShowStaffProfile(selectedPerson.SchoolId, selectedPerson.StaffId);
        }), tone: "secondary"));
        if (_actor.Role is not (Roles.Entry or Roles.Principal))
        {
            var conflicts = _store.GetDistrictStaffCodeConflicts(_actor);
            if (conflicts.Count > 0)
            {
                unified.Children.Add(Label(
                    $"مراجعة سلامة السجلات — يوجد {conflicts.Count} رقم وظيفي مكرر بين مدارس مختلفة، ولم يتم دمج أي سجل تلقائيا.", 15, true));
                unified.Children.Add(GridFor(conflicts,
                    ("الرقم الوظيفي المتكرر", "EmployeeCode", false),
                    ("عدد المدارس", "Schools", false)));
                unified.Children.Add(Label(
                    "صحح أرقام الكادر بعد مراجعة المستندات؛ لا تنقل بيانات الانتداب إلى مدرسة ثانية لإنشاء موظف مكرر.", 14));
            }
        }
        root.Children.Add(Box(unified));
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
        root.Children.Add(Box(TableSurface(staffGrid,
            "لا توجد سجلات كادر مطابقة في المدرسة المختارة.")));
        var staffSearch = Field(_navigationContext.Get("staff.local-search"), width: 340);
        staffSearch.ToolTip = "ابحث بالاسم أو الرقم الوظيفي أو التخصص أو المؤهل";
        root.Children.Add(Box(LineOfFields(("بحث سريع في كشف الكادر", staffSearch))));
        IReadOnlyList<SchoolStaff> staffCache = Array.Empty<SchoolStaff>();
        root.Children.Add(Label("انقر مرتين على اسم المعلم أو الإداري لفتح ملفه الوظيفي وتفاصيل الانتداب والتعاقد.", 13));
        staffGrid.MouseDoubleClick += (_, e) =>
        {
            if (IsDoubleClickOnGridRow(e.OriginalSource, staffGrid) &&
                staffGrid.SelectedItem is SchoolStaff selectedStaff &&
                schoolBox.SelectedItem is School chosenSchool)
                Safe(() => ShowStaffProfile(chosenSchool.Id, selectedStaff.Id));
        };
        root.Children.Add(Btn("فتح ملف الموظف المحدد", (_, _) => Safe(() =>
        {
            if (staffGrid.SelectedItem is not SchoolStaff selectedStaff ||
                schoolBox.SelectedItem is not School chosenSchool)
                throw new InvalidOperationException("حدد الموظف المطلوب فتح ملفه من الجدول.");
            ShowStaffProfile(chosenSchool.Id, selectedStaff.Id);
        }), tone: "secondary"));

        var editor = Stack();
        editor.Children.Add(Label("إضافة موظف أو تعديل الاسم والبيانات: اختر السجل من الجدول، صحح الحقول ثم اضغط حفظ الموظف.", 16, true));
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
        var active = new CheckBox { Content = "السجل نشط في كشوف المدرسة (الانتداب يسجل في الملف الوظيفي)",
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
        var validation = new InlineValidationPresenter();
        editor.Children.Add(validation.Message);
        var staffFields = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase)
        {
            ["employee_code"] = employeeCode, ["full_name"] = fullName,
            ["category"] = category, ["gender"] = gender
        };
        SchoolStaff? selected = null;

        void Clear()
        {
            selected = null;
            staffGrid.SelectedItem = null;
            employeeCode.Text = ""; fullName.Text = ""; job.Text = "";
            specialization.Text = ""; qualification.Text = "";
            category.SelectedIndex = 0; gender.SelectedIndex = 2; active.IsChecked = true;
            validation.Clear();
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
            staffCache = _store.GetSchoolStaff(_actor, school.Id);
            staffGrid.ItemsSource = DirectorySearch.FindStaff(staffCache, staffSearch.Text);
            editor.Visibility = AccessPolicy.CanEditStaff(_actor, school.Id)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        schoolBox.SelectionChanged += (_, _) =>
        {
            if (schoolBox.SelectedItem is School school)
                _navigationContext.Set("staff.school", school.Id);
            Safe(Refresh);
        };
        staffSearch.TextChanged += (_, _) =>
        {
            _navigationContext.Set("staff.local-search", staffSearch.Text);
            Clear();
            staffGrid.ItemsSource = DirectorySearch.FindStaff(staffCache, staffSearch.Text);
        };

        var actions = new WrapPanel();
        actions.Children.Add(Btn("سجل جديد", (_, _) => Clear()));
        actions.Children.Add(Btn("حفظ الموظف", (_, _) =>
        {
            if (schoolBox.SelectedItem is not School school)
            {
                validation.ShowError("حدد المدرسة أولا.");
                return;
            }
            if (!validation.Show(StaffFormValidator.Validate(employeeCode.Text, fullName.Text,
                    category.SelectedItem?.ToString(), gender.SelectedItem?.ToString()),
                    staffFields)) return;
            try
            {
                var staff = new SchoolStaff(selected?.Id ?? "", school.Id, employeeCode.Text,
                    fullName.Text, category.SelectedItem?.ToString() ?? "", job.Text,
                    specialization.Text, qualification.Text,
                    gender.SelectedItem?.ToString() ?? "غير محدد", active.IsChecked == true);
                _store.SaveSchoolStaff(_actor, staff);
                Info("تم حفظ سجل الموظف وتحديث كشف المدرسة والدليل الموحد.");
                ShowStaffCore(school.Id);
            }
            catch (Exception ex) { validation.ShowError(ex.Message); }
        }));
        actions.Children.Add(Btn("حذف الموظف من الكشوف (أرشفة)", (_, _) => Safe(() =>
        {
            if (selected is null) throw new InvalidOperationException("حدد الموظف من كشف الكادر أولا.");
            if (MessageBox.Show("سيتم استبعاده من أعداد الموظفين النشطين مع الحفاظ على ملفه وسجله الوظيفي. هل تؤكد؟",
                "تأكيد أرشفة موظف", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _store.SaveSchoolStaff(_actor, selected with { IsActive = false });
            ShowStaffCore(selected.SchoolId);
        }), tone: "danger"));
        actions.Children.Add(Btn("استعادة موظف مؤرشف", (_, _) => Safe(() =>
        {
            if (selected is null) throw new InvalidOperationException("حدد الموظف المؤرشف أولا.");
            _store.SaveSchoolStaff(_actor, selected with { IsActive = true });
            ShowStaffCore(selected.SchoolId);
        }), tone: "secondary"));
        editor.Children.Add(actions);

        var importPanel = Stack();
        importPanel.Children.Add(Label("استيراد سجلات المدارس من Excel أو Word", 16, true));
        importPanel.Children.Add(Label(
            "يدعم الجداول المنظمة .xlsx و.docx. يجب أن يحتوي كل صف على رمز المدرسة والرقم الوظيفي والاسم والفئة؛ يتم التوجيه برمز المدرسة وليس بالتخمين من اسم الملف. السجلات الموجودة بالرقم نفسه سيتم تحديثها بعد التأكيد.", 13));
        importPanel.Children.Add(Btn("فتح المعالج الآمن لاستيراد الكادر",
            (_,_) => ShowImportWizard(ImportEntity.Staff,
                (schoolBox.SelectedItem as School)?.Id),tone:"secondary"));
        schoolBox.SelectionChanged += (_, _) =>
        {
            if (schoolBox.SelectedItem is School school)
                importPanel.Visibility = AccessPolicy.CanEditStaff(_actor, school.Id)
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        root.Children.Add(Box(importPanel));
        root.Children.Add(Box(editor));
        Display("كادر المدارس والسجلات", root);
        schoolBox.SelectedItem = schools.FirstOrDefault(s => s.Id ==
            (preferredSchoolId ?? _navigationContext.Get("staff.school"))) ?? schools[0];
    }

    private sealed class EnrollmentInput
    {
        public string Grade { get; set; } = "";
        public string GradeName { get; set; } = "";
        public string Boys { get; set; } = "";
        public string Girls { get; set; } = "";
    }

    private static int Count(string text, string label) =>
        CensusNumberInput.ParseNonNegative(text, label);

    private static int[] GradesFor(School school)
    {
        return GradeNames.Allowed(school.Stage).ToArray();
    }

    private void ShowCensus() => ShowCensusCore(null);

    private void ShowCensusCore(string? preferredSchoolId)
    {
        var schools = _store.GetSchools(_actor).Where(s => s.IsActive).ToList();
        var root = Stack();
        if (schools.Count == 0)
        {
            root.Children.Add(Label("لا توجد مدارس نشطة متاحة لهذا الحساب."));
            Display("إحصاء الطلاب والمعلمين", root);
            return;
        }

        root.Children.Add(Label("الاستمارة الإحصائية للمدرسة", 20, true));
        root.Children.Add(Label(
            "أدخل أعداد الطلاب لكل صف. يمكن كتابة الأرقام العربية (١٢٣) أو الإنجليزية (123)، وترك الحقول غير المستخدمة فارغة لتساوي صفرا.", 14));
        var combo = new ComboBox
        {
            ItemsSource = schools, DisplayMemberPath = "Name", Width = 390,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(Box(LineOfFields(("اختر المدرسة", combo))));

        var form = Stack();
        var status = Label("", 15, true);
        status.Foreground = new SolidColorBrush(Color.FromRgb(9, 97, 130));
        form.Children.Add(status);
        // Ordinary field errors are shown in the current form, without a blocking
        // Windows MessageBox. Programming errors still go through normal diagnostics.
        void ValidateCensus(Action action)
        {
            try
            {
                action();
                status.Foreground = new SolidColorBrush(Color.FromRgb(9, 109, 83));
            }
            catch (ArgumentException ex)
            {
                status.Foreground = new SolidColorBrush(Color.FromRgb(170, 45, 62));
                status.Text = "راجع المدخلات: " + ex.Message;
                ContentScroll.ScrollToTop();
            }
            catch (InvalidOperationException ex)
            {
                status.Foreground = new SolidColorBrush(Color.FromRgb(170, 45, 62));
                status.Text = "تعذر تنفيذ الإجراء: " + ex.Message;
                ContentScroll.ScrollToTop();
            }
            catch (UnauthorizedAccessException ex)
            {
                status.Foreground = new SolidColorBrush(Color.FromRgb(170, 45, 62));
                status.Text = "الصلاحية غير متاحة: " + ex.Message;
                ContentScroll.ScrollToTop();
            }
        }
        var editable = AccessPolicy.Has(_actor, PermissionFlags.EditCensus);
        var canReadRosters = AccessPolicy.Has(_actor, PermissionFlags.ViewStudents);
        var lines = new ObservableCollection<EnrollmentInput>();

        form.Children.Add(Label("1. أعداد الطلاب حسب الصف", 18, true));
        form.Children.Add(Label(
            "اترك الصف الذي لا توجد له بيانات فارغا، ويمكن ترك خانة الذكور أو الإناث فارغة عندما يكون العدد صفرا.", 13));
        var table = GridFor(lines,
            ("المرحلة / الصف", "GradeName", false),
            ("الذكور", "Boys", true), ("الإناث", "Girls", true));
        table.IsReadOnly = !editable;
        table.MaxHeight = 410;
        form.Children.Add(table);

        form.Children.Add(Label("2. المعلمون والشعب الدراسية", 18, true));
        form.Children.Add(Label(
            "المقصود بالفصل هنا شعبة دراسية من صف معين، مثل أولى ثانوي / أ أو أولى ثانوي / ب؛ كل شعبة تُحسب فصلا مستقلا. لا يقصد عدد غرف المبنى.", 14));

        var male = Field(width: 145);
        var female = Field(width: 145);
        var rooms = Field(width: 170);
        male.ToolTip = "عدد المعلمين الذكور؛ الخانة الفارغة تعني صفر";
        female.ToolTip = "عدد المعلمات؛ الخانة الفارغة تعني صفر";
        rooms.ToolTip = "عدد الشعب/الفصول التعليمية، وليس عدد غرف المبنى؛ يمكنك تعبئته تلقائيا من كشوف المدرسة";
        male.IsReadOnly = !editable;
        female.IsReadOnly = !editable;
        rooms.IsReadOnly = !editable;
        form.Children.Add(LineOfFields(
            ("المعلمون الذكور", male), ("المعلمات", female),
            ("عدد الفصول التعليمية (الشعب)", rooms)));

        var roomsHint = Label("", 13);
        roomsHint.Foreground = new SolidColorBrush(Color.FromRgb(26, 111, 127));
        form.Children.Add(roomsHint);
        var assist = new WrapPanel { Margin = new Thickness(0, 5, 0, 10) };
        assist.Children.Add(Btn("تعبئة عدد الشعب من كشوف المدرسة", (_, _) => ValidateCensus(() =>
        {
            if (combo.SelectedItem is not School selected) return;
            var sections = _store.GetSchoolGrades(_actor, selected.Id);
            var count = CensusNumberInput.CountRegisteredSections(sections);
            if (count == 0)
            {
                roomsHint.Text = "لا توجد شعب مسجلة لهذه المدرسة في العام الحالي؛ يمكنك إدخال العدد يدويا أو إضافة الشعب من صفحة الطلاب والصفوف.";
                return;
            }
            rooms.Text = count.ToString(CultureInfo.InvariantCulture);
            roomsHint.Text = $"تم اقتراح {count} فصلا من كشوف الصفوف والشعب. راجع العدد قبل حفظ المسودة؛ لم تتغير الاستمارة الرسمية بعد.";
        }), editable && canReadRosters, "secondary"));
        assist.Children.Add(Btn("تعبئة أعداد الطلاب من الكشوف الحالية", (_, _) => ValidateCensus(() =>
        {
            if (combo.SelectedItem is not School selected) return;
            var suggested = CensusNumberInput.SuggestGradeTotals(
                _store.GetStudents(_actor, selected.Id), _store.AcademicYear);
            if (suggested.Count == 0)
            {
                roomsHint.Text = "لا توجد سجلات طلاب نشطة في العام الدراسي الحالي يمكن الاستفادة منها.";
                return;
            }
            if (MessageBox.Show(
                "سيتم تعبئة أعداد الصفوف التي لها طلاب نشطون من كشوف هذه المدرسة، مع الإبقاء على بقية الصفوف دون تغيير. لن تُحفظ الاستمارة حتى تضغط «حفظ المسودة». هل تواصل؟",
                "مراجعة الأعداد قبل التعبئة", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var line in lines)
            {
                var grade = int.Parse(line.Grade, CultureInfo.InvariantCulture);
                if (!suggested.TryGetValue(grade, out var totals)) continue;
                line.Boys = totals.Boys.ToString(CultureInfo.InvariantCulture);
                line.Girls = totals.Girls.ToString(CultureInfo.InvariantCulture);
            }
            table.Items.Refresh();
            roomsHint.Text = "تمت تعبئة أعداد الطلاب النشطين في الصفوف ذات السجلات؛ راجعها ثم احفظ المسودة للمراجعة الرسمية.";
        }), editable && canReadRosters, "secondary"));
        form.Children.Add(assist);

        form.Children.Add(Label("3. ملاحظات المدرسة", 17, true));
        var notes = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 80, MaxHeight = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsReadOnly = !editable
        };
        form.Children.Add(notes);

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(Btn("حفظ المسودة", (_, _) => ValidateCensus(() =>
        {
            if (combo.SelectedItem is not School selected) return;
            table.CommitEdit(DataGridEditingUnit.Cell, true);
            table.CommitEdit(DataGridEditingUnit.Row, true);
            var rows = new List<EnrollmentLine>();
            foreach (var item in lines)
            {
                if (string.IsNullOrWhiteSpace(item.Boys) && string.IsNullOrWhiteSpace(item.Girls))
                    continue;
                rows.Add(new EnrollmentLine(item.Grade,
                    Count(item.Boys, "ذكور صف " + item.GradeName),
                    Count(item.Girls, "إناث صف " + item.GradeName)));
            }
            var census = new CensusData(rows,
                Count(male.Text, "المعلمين الذكور"),
                Count(female.Text, "المعلمات"),
                Count(rooms.Text, "عدد الفصول التعليمية (الشعب)"),
                notes.Text);
            _store.SaveDraft(_actor, selected.Id, census);
            var latest = _store.GetLatestSubmission(_actor, selected.Id);
            status.Text = latest is null
                ? "تم حفظ البيانات بنجاح."
                : $"تم حفظ المسودة بنجاح — المراجعة {latest.Revision}. يمكن الآن رفعها للمراجعة.";
        }), editable, "success"));
        actions.Children.Add(Btn("رفع المسودة للمراجعة", (_, _) => ValidateCensus(() =>
        {
            if (combo.SelectedItem is not School selected) return;
            var latest = _store.GetLatestSubmission(_actor, selected.Id)
                ?? throw new InvalidOperationException("احفظ المسودة أولا قبل رفعها للمراجعة.");
            _store.ChangeStatus(_actor, latest.Id, "submit");
            ShowCensusCore(selected.Id);
        }), editable, "warning"));
        form.Children.Add(actions);
        root.Children.Add(Box(form));

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not School school) return;
            lines.Clear();
            var latest = _store.GetLatestSubmission(_actor, school.Id);
            foreach (var grade in GradesFor(school))
            {
                var previous = latest?.Data.Rows.FirstOrDefault(x =>
                    x.Grade == grade.ToString(CultureInfo.InvariantCulture));
                lines.Add(new EnrollmentInput
                {
                    Grade = grade.ToString(CultureInfo.InvariantCulture),
                    GradeName = GradeNames.ForStage(school.Stage, grade),
                    Boys = previous?.Boys.ToString(CultureInfo.InvariantCulture) ?? "",
                    Girls = previous?.Girls.ToString(CultureInfo.InvariantCulture) ?? ""
                });
            }
            male.Text = latest?.Data.MaleTeachers.ToString(CultureInfo.InvariantCulture) ?? "";
            female.Text = latest?.Data.FemaleTeachers.ToString(CultureInfo.InvariantCulture) ?? "";
            rooms.Text = latest?.Data.Classrooms.ToString(CultureInfo.InvariantCulture) ?? "";
            notes.Text = latest?.Data.Notes ?? "";
            status.Text = latest is null ? "لم تبدأ الاستمارة — أدخل الأعداد أو استخدم تعبئة الكشوف." :
                $"الحالة: {StatusName(latest.Status)} — المراجعة رقم {latest.Revision}" +
                (latest.DecisionReason is null ? "" : $" — السبب: {latest.DecisionReason}");
            if (canReadRosters)
            {
                var count = CensusNumberInput.CountRegisteredSections(
                    _store.GetSchoolGrades(_actor, school.Id));
                if (count > 0)
                {
                    if (latest is null && editable)
                        rooms.Text = count.ToString(CultureInfo.InvariantCulture);
                    roomsHint.Text = $"تضم كشوف هذه المدرسة {count} شعبة مسجلة للعام الحالي." +
                        (latest is null ? " اقترحنا العدد في الحقل ويمكنك تعديله." :
                            " يبقى العدد المحفوظ في الاستمارة كما هو حتى تختار تحديثه.");
                }
                else
                    roomsHint.Text = "لم تُسجل شعب للعام الحالي؛ أدخل عدد الفصول يدويا، أو أضفها في صفحة الطلاب والصفوف ثم استخدم زر التعبئة.";
            }
            else
                roomsHint.Text = "يمكن إدخال عدد الفصول يدويا؛ جلب الشعب من سجلات الطلاب يتطلب صلاحية الاطلاع عليها.";
        };
        Display("إحصاء الطلاب والمعلمين", root);
        combo.SelectedItem = schools.FirstOrDefault(x => x.Id == preferredSchoolId) ?? schools[0];
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
        root.Children.Add(Btn("تصدير التقرير إلى CSV متوافق مع إكسل", (_, _) => Safe(() =>
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
    private sealed record AccountTableRow(UserAccount Account, string RoleDisplayName, string SchoolDisplayName)
    {
        public string Username => Account.Username;
        public string IsOwner => Account.IsOwner ? "نعم" : "لا";
        public string IsActive => Account.IsActive ? "نشط" : "موقوف";
    }

    private void ShowUsers()
    {
        if (!AccessPolicy.CanCreateUsers(_actor)) throw new UnauthorizedAccessException();
        var root = Stack();
        root.Children.Add(Label("إدارة المسؤولين والمستخدمين — الصلاحيات المحددة لكل حساب", 18, true));
        root.Children.Add(Label("المسؤول المؤسس محمي ولا يمكن إيقافه أو تغيير صلاحياته. المسؤولون المفوضون لا يستطيعون إنشاء مسؤولين آخرين أو منح صلاحيات أكبر من صلاحياتهم.", 13));

        var users = _store.GetUsers(_actor);
        var schoolsById = _store.GetSchools(_actor).ToDictionary(x => x.Id, x => x.Name);
        var accountRows = users.Select(u => new AccountTableRow(u, RoleName(u.Role),
            u.SchoolId is null ? "إدارة التربية بالمديرية" :
                schoolsById.GetValueOrDefault(u.SchoolId, "مدرسة غير متاحة"))).ToArray();
        var grid = GridFor(accountRows,
            ("الحساب", "Username", false), ("الدور", "RoleDisplayName", false),
            ("المدرسة", "SchoolDisplayName", false), ("المسؤول المؤسس", "IsOwner", false),
            ("الحالة", "IsActive", false));
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

        form.Children.Add(Label("إعداد حساب مدرسة جاهز — اختر المدرسة المطلوبة ثم نوع الصلاحية:", 15, true));
        var presets = new WrapPanel();
        presets.Children.Add(Btn("مستخدم مدرسة — مشاهدة فقط", (_, _) =>
        {
            if (selected is not null) { Info("اختر «حساب جديد» أولا لتطبيق هذا النموذج."); return; }
            role.SelectedValue = Roles.Entry;
            SetPermissions((long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStaff |
                PermissionFlags.ViewReports | PermissionFlags.ViewStudents));
        }));
        presets.Children.Add(Btn("مستخدم مدرسة — مشاهدة وتعديل وإضافة", (_, _) =>
        {
            if (selected is not null) { Info("اختر «حساب جديد» أولا لتطبيق هذا النموذج."); return; }
            role.SelectedValue = Roles.Entry;
            SetPermissions((long)(PermissionFlags.ViewSchools | PermissionFlags.ViewReports | PermissionFlags.EditCensus |
                PermissionFlags.ViewStaff | PermissionFlags.ManageStaff | PermissionFlags.ManageSchoolProfile |
                PermissionFlags.ViewStudents | PermissionFlags.ManageStudents | PermissionFlags.ManageStudentEvaluation));
        }));
        form.Children.Add(presets);
        form.Children.Add(Label("لإضافة مسؤول في إدارة التربية اختر «مدير النظام»، ثم امنحه صلاحياته التفصيلية. حسابات المدرسة لا ترى سوى المدرسة المرتبطة بها.", 13));

        grid.SelectionChanged += (_, _) =>
        {
            selected = (grid.SelectedItem as AccountTableRow)?.Account;
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
