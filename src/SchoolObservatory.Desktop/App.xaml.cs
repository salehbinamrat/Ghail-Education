using System.IO;
using System.Windows;
using SchoolObservatory.Core;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SchoolObservatory");
            var store = new SqliteStore(Path.Combine(root, "observatory.db"));
            store.Initialize();

            string? username = null;
            if (!store.HasUsers())
            {
                var setup = new SetupWindow(store);
                var result = setup.ShowDialog();
                if (result != true && !store.HasUsers())
                {
                    Shutdown();
                    return;
                }
                username = setup.CreatedUsername;
            }

            ShowLogin(store, username);
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر بدء المرصد المدرسي: " + ex.Message,
                "المرصد المدرسي", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ShowLogin(SqliteStore store, string? suggestedUsername)
    {
        try
        {
            var login = new LoginWindow(store, suggestedUsername);
            if (login.ShowDialog() != true || login.SignedInUser is not UserAccount user)
            {
                Shutdown();
                return;
            }

            var main = new MainWindow(store, user);
            MainWindow = main;
            main.Closed += (_, _) =>
            {
                if (main.LogoutRequested)
                {
                    // Keep the application running and prompt for a different account.
                    Dispatcher.BeginInvoke(new Action(() => ShowLogin(store, user.Username)));
                }
                else Shutdown();
            };
            main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر فتح واجهة البرنامج: " + ex.Message,
                "المرصد المدرسي", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
