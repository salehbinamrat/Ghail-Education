using System.Windows;
using System.Windows.Input;
using SchoolObservatory.Core;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop;

public partial class LoginWindow : Window
{
    private readonly SqliteStore _store;
    private bool _authenticating;
    public UserAccount? SignedInUser { get; private set; }

    public LoginWindow(SqliteStore store, string? suggestedUsername = null)
    {
        InitializeComponent();
        _store = store;
        UsernameBox.Text = suggestedUsername ?? string.Empty;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowLayout.FitToWorkArea(this);
        if (string.IsNullOrWhiteSpace(UsernameBox.Text)) UsernameBox.Focus();
        else PasswordInput.Focus();
    }

    private async void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await LoginAsync();
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await LoginAsync();

    private async Task LoginAsync()
    {
        if (_authenticating) return;
        var username = UsernameBox.Text.Trim();
        var password = PasswordInput.Password;
        if (username.Length == 0 || password.Length == 0)
        {
            ErrorText.Text = "أدخل اسم المستخدم وكلمة المرور قبل المتابعة.";
            return;
        }
        _authenticating = true;
        LoginButton.IsEnabled = false;
        ErrorText.Text = "جار التحقق من بيانات الدخول...";
        try
        {
            SignedInUser = await Task.Run(() => _store.Authenticate(username, password));
            PasswordInput.Clear();
            if (SignedInUser is null)
            {
                ErrorText.Text = "اسم المستخدم أو كلمة المرور غير صحيحة، أو الحساب معطل أو مقفل مؤقتا.";
                PasswordInput.Focus();
            }
            else DialogResult = true;
        }
        catch (Exception)
        {
            PasswordInput.Clear();
            ErrorText.Text = "تعذر إكمال تسجيل الدخول. تحقق من صلاحية قاعدة البيانات وحاول مجددا.";
            PasswordInput.Focus();
        }
        finally
        {
            _authenticating = false;
            LoginButton.IsEnabled = true;
        }
    }
}
