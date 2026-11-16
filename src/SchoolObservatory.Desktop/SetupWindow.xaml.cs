using System.Windows;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop;

public partial class SetupWindow : Window
{
    private readonly SqliteStore _store;
    public SetupWindow(SqliteStore store) { InitializeComponent(); _store = store; }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PasswordInput.Password != ConfirmInput.Password)
                throw new ArgumentException("كلمتا المرور غير متطابقتين.");
            _store.InitializeAdministrator(DistrictBox.Text, YearBox.Text, UsernameBox.Text, PasswordInput.Password);
            MessageBox.Show("تمت التهيئة. سجل الدخول بحساب المسؤول الذي أنشأته.", "المرصد المدرسي");
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "التحقق من البيانات", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
