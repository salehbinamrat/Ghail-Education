using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
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
            var store = StartEncryptedStore(Path.Combine(root, "observatory.db"));

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

    private SqliteStore StartEncryptedStore(string path)
    {
        try
        {
            var store = new SqliteStore(path);
            store.Initialize();
            return store;
        }
        catch (Exception ex) when (File.Exists(path) &&
            (ex is SqliteException or CryptographicException or InvalidDataException or IOException))
        {
            var answer = MessageBox.Show(
                "قاعدة البيانات المحلية غير قابلة للفتح أو التحقق. لن يتم إنشاء قاعدة بديلة تلقائيا. " +
                "يمكنك الاستعادة من نسخة مشفرة موثوقة بكلمة استعادة مستقلة؛ ستُحفَظ القاعدة الأصلية ومفتاحها للاسترجاع. هل تريد الاستعادة؟",
                "استعادة طوارئ دون إنترنت", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) throw;
            var picker = new OpenFileDialog
            {
                Title = "اختر نسخة مشفرة سليمة لاستعادة قاعدة البيانات",
                Filter = "School Observatory backup (*.sobak)|*.sobak",
                CheckFileExists = true
            };
            if (picker.ShowDialog() != true) throw;
            var prompt = new RecoveryPasswordDialog();
            if (prompt.ShowDialog() != true) throw;
            if (MessageBox.Show(
                "تأكيد نهائي: استعادة قاعدة مشفرة من نسخة احتياطية. ستُحفَظ ملفات القاعدة غير القابلة للفتح في مجلد recovery. تابع؟",
                "تأكيد استعادة الطوارئ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                throw;
            OfflineDisasterRecovery.RecoverUnreadableDatabase(path, picker.FileName, prompt.RecoveryPassword, confirmed: true);
            var recovered = new SqliteStore(path);
            recovered.Initialize();
            return recovered;
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
