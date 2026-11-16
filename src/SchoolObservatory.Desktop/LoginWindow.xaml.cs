using System.Windows;
using System.Windows.Input;
using SchoolObservatory.Core;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop;

public partial class LoginWindow : Window
{
    private readonly SqliteStore _store;
    public UserAccount? SignedInUser { get; private set; }
    public LoginWindow(SqliteStore store) { InitializeComponent(); _store = store; }
    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Login();
    }
    private void Login_Click(object sender, RoutedEventArgs e) => Login();
    private void Login()
    {
        SignedInUser = _store.Authenticate(UsernameBox.Text, PasswordInput.Password);
        PasswordInput.Clear();
        if (SignedInUser is null)
            MessageBox.Show("بيانات الدخول غير صحيحة أو الحساب مقفل مؤقتا.", "تسجيل الدخول", MessageBoxButton.OK, MessageBoxImage.Warning);
        else DialogResult = true;
    }
}
