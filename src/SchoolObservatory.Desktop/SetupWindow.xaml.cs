using System.Windows;
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
