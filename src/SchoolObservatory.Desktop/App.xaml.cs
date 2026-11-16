using System.IO;
using System.Windows;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchoolObservatory");
            var store = new SqliteStore(Path.Combine(root, "observatory.db"));
            store.Initialize();
            if (!store.HasUsers())
            {
                var setup = new SetupWindow(store);
                if (setup.ShowDialog() != true) { Shutdown(); return; }
            }
            var login = new LoginWindow(store);
            if (login.ShowDialog() != true || login.SignedInUser is null) { Shutdown(); return; }
            var main = new MainWindow(store, login.SignedInUser);
            MainWindow = main;
            main.Closed += (_, _) => Shutdown();
            main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر فتح قاعدة البيانات: " + ex.Message, "المرصد المدرسي", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
