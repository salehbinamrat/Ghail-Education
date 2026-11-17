using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private DispatcherTimer? _automaticBackupTimer;
    private bool _automaticBackupBusy;

    private void StartAutomaticBackup()
    {
        if (!_actor.IsOwner) return;
        _automaticBackupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _automaticBackupTimer.Tick += async (_, _) =>
        {
            if (_automaticBackupBusy) return;
            _automaticBackupBusy = true;
            try
            {
                var result = await Task.Run(() => _store.RunAutomaticBackupIfDue(_actor));
                if (result is not null)
                    MessageBox.Show("تم حفظ نسخة احتياطية مشفرة والتحقق منها:\n" + result,
                        "النسخ الاحتياطي", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                // Do not swallow failed backups and falsely claim protection.
                MessageBox.Show("فشل النسخ الاحتياطي التلقائي. تحقق من مسار الحفظ والمساحة وكلمة الاستعادة:\n" +
                    ex.Message, "تنبيه أمان", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _automaticBackupBusy = false; }
        };
        _automaticBackupTimer.Start();
        Closed += (_, _) => _automaticBackupTimer?.Stop();
    }

    private void ShowBackups()
    {
        if (!_actor.IsOwner) throw new UnauthorizedAccessException();
        var root = Stack();
        root.Children.Add(Label("النسخ والاستعادة — للمسؤول المؤسس فقط", 20, true));
        root.Children.Add(Label("النسخ مشفرة بصيغة SQLCipher 4، وتحتوي على جميع الجداول وسجل العمليات. احتفظ بكلمة استعادة مستقلة في مكان آمن خارج الجهاز. استعادة نسخة قديمة تستبدل البيانات الحالية بالكامل.", 14));

        var password = new PasswordBox { MaxLength = 256, Width = 390, HorizontalAlignment = HorizontalAlignment.Left };
        var section = Stack();
        section.Children.Add(Label("كلمة مرور الاستعادة (16 حرفا على الأقل)"));
        section.Children.Add(password);
        var chosen = "";
        var state = Label("لم يتم اختيار نسخة للاستعادة.", 13);
        state.TextWrapping = TextWrapping.Wrap;

        var actions = new WrapPanel();
        actions.Children.Add(Btn("إنشاء نسخة احتياطية مشفرة", async (_, _) =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "حفظ نسخة البيانات المشفرة", Filter = "School Observatory backup (*.sobak)|*.sobak",
                DefaultExt = ".sobak", AddExtension = true,
                FileName = "SchoolObservatory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".sobak"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                state.Text = "جار إنشاء النسخة والتحقق منها...";
                var secret = password.Password;
                await Task.Run(() => _store.CreateEncryptedBackup(_actor, dialog.FileName, secret));
                state.Text = "اكتمل النسخ والتحقق: " + dialog.FileName;
                MessageBox.Show("تم إنشاء نسخة مشفرة وتدقيق سجلاتها. احتفظ بكلمة مرور الاستعادة في مكان آمن.",
                    "نجاح النسخ", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { state.Text = "فشل إنشاء النسخة: " + ex.Message; }
        }, tone: "success"));
        actions.Children.Add(Btn("اختيار نسخة ومعاينة محتواها", async (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "اختيار نسخة مشفرة للتحقق", Filter = "School Observatory backup (*.sobak)|*.sobak",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var secret = password.Password;
                var preview = await Task.Run(() => _store.PreviewEncryptedBackup(_actor, dialog.FileName, secret));
                chosen = dialog.FileName;
                state.Text = "نسخة متحقّق منها: " + preview.FileName + "\n" +
                    string.Join(" | ", preview.RowCounts.Select(x => x.Key + ": " + x.Value));
            }
            catch (Exception ex)
            {
                chosen = "";
                state.Text = "تعذر التحقق من النسخة أو كلمة المرور: " + ex.Message;
            }
        }));
        actions.Children.Add(Btn("استعادة النسخة المحددة", async (_, _) =>
        {
            if (string.IsNullOrEmpty(chosen))
            {
                state.Text = "افتح النسخة وتحقق منها أولا.";
                return;
            }
            if (MessageBox.Show(
                "تحذير: سيتم استبدال قاعدة البيانات الحالية بالكامل بالنسخة المختارة، ثم إغلاق البرنامج. سيتم الاحتفاظ بالقاعدة الحالية كنسخة رجوع مشفرة. هل تؤكد الاستعادة؟",
                "تأكيد نهائي لاستعادة البيانات", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                var secret = password.Password;
                state.Text = "جار استعادة النسخة والتحقق من سلامة جميع السجلات...";
                await Task.Run(() => _store.RestoreEncryptedBackup(_actor, chosen, secret, confirmed: true));
                MessageBox.Show("نجحت الاستعادة والتحقق. أعد تشغيل البرنامج وسجل الدخول بالبيانات المستعادة.",
                    "استعادة ناجحة", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
            }
            catch (Exception ex) { state.Text = "فشلت الاستعادة، ولم يتم اعتماد النسخة الجديدة: " + ex.Message; }
        }, tone: "danger"));
        section.Children.Add(actions);
        section.Children.Add(state);
        root.Children.Add(Box(section));

        var auto = Stack();
        var settings = _store.GetAutomaticBackupSettings(_actor);
        auto.Children.Add(Label("النسخ التلقائي أثناء تشغيل البرنامج", 18, true));
        auto.Children.Add(Label("احفظ نسخة جديدة كل عدد محدد من الساعات أثناء تشغيل البرنامج بحساب المسؤول المؤسس. لا يحذف النظام النسخ القديمة تلقائيا.", 13));
        auto.Children.Add(Label("مجلد النسخ التلقائية"));
        var folder = Field(settings?.Directory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SchoolObservatoryBackups"), 480);
        auto.Children.Add(folder);
        auto.Children.Add(Label("عدد الساعات بين النسخ (1 إلى 168)"));
        var hours = Field((settings?.IntervalHours ?? 24).ToString(CultureInfo.InvariantCulture), 120);
        auto.Children.Add(hours);
        var autoActions = new WrapPanel();
        autoActions.Children.Add(Btn("تفعيل أو تحديث النسخ التلقائي", (_, _) => Safe(() =>
        {
            if (!int.TryParse(hours.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var interval))
                throw new ArgumentException("أدخل عددا صحيحا للساعات.");
            _store.ConfigureAutomaticBackup(_actor, folder.Text, interval, password.Password);
            state.Text = "تم حفظ إعدادات النسخ التلقائي داخل مخزن مشفر محمي بحساب ويندوز.";
        })));
        autoActions.Children.Add(Btn("إيقاف النسخ التلقائي", (_, _) => Safe(() =>
        {
            _store.DisableAutomaticBackup(_actor);
            state.Text = "تم إيقاف النسخ التلقائي. النسخ السابقة لم تُحذف.";
        }), tone: "warning"));
        auto.Children.Add(autoActions);
        root.Children.Add(Box(auto));
        Display("النسخ الاحتياطي والاستعادة", root);
    }
}
