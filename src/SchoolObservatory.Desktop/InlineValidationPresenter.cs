using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

/// <summary>Non-modal, screen-reader announced form validation; authoritative store validates again.</summary>
internal sealed class InlineValidationPresenter
{
    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 6, 8, 6),
        Foreground = new SolidColorBrush(Color.FromRgb(161, 30, 53)),
        FontFamily = new FontFamily("Arial"),
        FontWeight = FontWeights.Bold,
        FontSize = 13
    };
    private readonly Dictionary<Control, Brush> _originalBorders = new();

    public TextBlock Message => _message;

    public InlineValidationPresenter()
    {
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Assertive);
        AutomationProperties.SetName(_message, "رسائل التحقق من بيانات النموذج");
    }

    public bool Show(IReadOnlyList<UiValidationIssue> issues,
        IReadOnlyDictionary<string, Control> fields)
    {
        Clear();
        if (issues.Count == 0) return true;
        _message.Text = string.Join("  |  ", issues.Select(issue => issue.Message));
        foreach (var issue in issues)
        {
            if (!fields.TryGetValue(issue.Field, out var field)) continue;
            if (!_originalBorders.ContainsKey(field))
                _originalBorders[field] = field.BorderBrush;
            field.BorderBrush = new SolidColorBrush(Color.FromRgb(161, 30, 53));
            AutomationProperties.SetHelpText(field, issue.Message);
        }
        if (fields.TryGetValue(issues[0].Field, out var first))
            first.Focus();
        return false;
    }

    public void ShowError(string message)
    {
        Clear();
        _message.Text = message;
    }

    public void Clear()
    {
        _message.Text = "";
        foreach (var (control, brush) in _originalBorders)
        {
            control.BorderBrush = brush;
            AutomationProperties.SetHelpText(control, "");
        }
        _originalBorders.Clear();
    }
}
