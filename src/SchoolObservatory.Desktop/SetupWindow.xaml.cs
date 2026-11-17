using System.Windows;
using Microsoft.Win32;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop;

public partial class SetupWindow : Window
{
    private readonly SqliteStore _store;
    public string? CreatedUsername { get; private set; }

    public SetupWindow(SqliteStore store)
    {
        InitializeComponent();
        _store = store;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowLayout.FitToWorkArea(this);
        DistrictBox.Focus();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_store.HasUsers())
            {
                MessageBox.Show("تم إعداد الحساب الأول بالفعل. استخدم تسجيل الدخول بحسابك الموجود.",
                    "المرصد المدرسي", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = false;
                return;
            }
            if (PasswordInput.Password != ConfirmInput.Password)
                throw new ArgumentException("كلمتا المرور غير متطابقتين.");
            var username = UsernameBox.Text.Trim();
            _store.InitializeAdministrator(DistrictBox.Text, YearBox.Text, username, PasswordInput.Password);
            CreatedUsername = username;
            PasswordInput.Clear();
            ConfirmInput.Clear();
            MessageBox.Show("تم إنشاء حساب المسؤول الأول بنجاح. سجل الدخول باسم المستخدم وكلمة المرور اللذين اخترتهما.",
                "المرصد المدرسي", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "التحقق من البيانات", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_store.HasUsers())
        {
            MessageBox.Show("استعادة قاعدة بها حسابات تتطلب مسؤول النظام من داخل البرنامج.");
            return;
        }
        var picker = new OpenFileDialog
        {
            Title = "اختيار نسخة مشفرة من جهاز Windows آخر",
            Filter = "School Observatory backup (*.sobak)|*.sobak",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        var secretPrompt = new RecoveryPasswordDialog { Owner = this };
        if (secretPrompt.ShowDialog() != true) return;
        if (MessageBox.Show(
            "سيتم استبدال قاعدة البيانات الجديدة الفارغة بكامل بيانات النسخة المشفرة. هل أنت متأكد؟",
            "استعادة حسابات وبيانات المدارس", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            _store.RestoreBeforeInitialSetup(picker.FileName, secretPrompt.RecoveryPassword, confirmed: true);
            MessageBox.Show("تمت الاستعادة. سجل الدخول بأحد حسابات قاعدة البيانات المستعادة.");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("فشلت الاستعادة دون استبدال قاعدة البيانات: " + ex.Message,
                "فشل الاستعادة", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExistingLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_store.HasUsers())
        {
            DialogResult = false; // The application checks HasUsers before opening login.
            return;
        }
        MessageBox.Show(
            "لم يتم إنشاء أي حساب مسؤول في قاعدة البيانات الحالية. أكمل التهيئة أولا، وبعدها ستفتح شاشة تسجيل الدخول تلقائيا. لن تُنشأ كلمة مرور افتراضية.",
            "إنشاء الحساب الأول مطلوب", MessageBoxButton.OK, MessageBoxImage.Information);
        SetupFieldsScroll.ScrollToTop();
        DistrictBox.Focus();
    }
}
