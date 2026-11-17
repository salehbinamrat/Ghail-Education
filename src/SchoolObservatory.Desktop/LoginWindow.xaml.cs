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
        // Only load the secret for the same account, if Windows has an opted-in entry.
        try
        {
            var saved = WindowsCredentialVault.Read();
            if (saved is not null &&
                (string.IsNullOrWhiteSpace(suggestedUsername) ||
                 string.Equals(suggestedUsername, saved.Value.Username,
                     StringComparison.OrdinalIgnoreCase)))
            {
                UsernameBox.Text = saved.Value.Username;
                PasswordInput.Password = saved.Value.Password;
                RememberCredentials.IsChecked = true;
                ForgetSavedButton.IsEnabled = true;
            }
            else ForgetSavedButton.IsEnabled = saved is not null;
        }
        catch (Exception)
        {
            ForgetSavedButton.IsEnabled = false;
            ErrorText.Text = "تعذر قراءة بيانات الدخول المحفوظة؛ يمكنك تسجيل الدخول يدويا.";
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowLayout.FitToWorkArea(this);
        if (string.IsNullOrWhiteSpace(UsernameBox.Text)) UsernameBox.Focus();
        else PasswordInput.Focus();
    }

    private void ForgetSaved_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowsCredentialVault.Delete();
            RememberCredentials.IsChecked = false;
            ForgetSavedButton.IsEnabled = false;
            PasswordInput.Clear();
            ErrorText.Text = "تم نسيان بيانات الدخول المحفوظة على هذا الجهاز.";
        }
        catch (Exception)
        {
            ErrorText.Text = "تعذر حذف بيانات الاعتماد من ويندوز. راجع مدير بيانات الاعتماد.";
        }
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
            else
            {
                try
                {
                    if (RememberCredentials.IsChecked == true)
                        WindowsCredentialVault.Save(username, password);
                    else
                        WindowsCredentialVault.Delete();
                }
                catch (Exception)
                {
                    MessageBox.Show("تم تسجيل الدخول بنجاح، لكن تعذر تحديث بيانات الدخول المحفوظة في ويندوز.",
                        "تنبيه حفظ بيانات الدخول", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                DialogResult = true;
            }
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
