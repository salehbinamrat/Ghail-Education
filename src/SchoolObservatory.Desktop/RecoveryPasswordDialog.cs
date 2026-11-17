using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SchoolObservatory.Desktop;

/// <summary>Transient password prompt; the recovery password is never saved or logged.</summary>
internal sealed class RecoveryPasswordDialog : Window
{
    private readonly PasswordBox _secret = new() { MaxLength = 256, MinHeight = 42, Width = 410 };
    public string RecoveryPassword => _secret.Password;

    public RecoveryPasswordDialog()
    {
        Title = "استعادة نسخة مشفرة على هذا الجهاز";
        Width = 520;
        Height = 270;
        MinWidth = 450;
        ResizeMode = ResizeMode.NoResize;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(234, 248, 252));
        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(new TextBlock
        {
            Text = "أدخل كلمة استعادة النسخة المشفرة:",
            FontWeight = FontWeights.Bold, FontSize = 17, Margin = new Thickness(0, 0, 0, 15)
        });
        stack.Children.Add(_secret);
        stack.Children.Add(new TextBlock
        {
            Text = "تستخدم لاستعادة جميع حسابات المدارس والطلاب والنتائج والصلاحيات؛ لا تُحفظ على هذا الجهاز.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 13)
        });
        var confirm = new Button
        {
            Content = "التحقق من كلمة الاستعادة والمتابعة",
            MinHeight = 44, Background = Brushes.Teal, Foreground = Brushes.White
        };
        confirm.Click += (_, _) =>
        {
            if (_secret.Password.Length == 0)
            {
                MessageBox.Show("أدخل كلمة الاستعادة أولا.");
                return;
            }
            DialogResult = true;
        };
        stack.Children.Add(confirm);
        Content = stack;
        Loaded += (_, _) => _secret.Focus();
    }
}
