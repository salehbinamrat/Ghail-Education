using System.Windows;

namespace SchoolObservatory.Desktop;

/// <summary>Keep dialogs usable at 100%, 125%, and 150% Windows display scaling.</summary>
internal static class WindowLayout
{
    public static void FitToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;

        var maxWidth = Math.Max(1, area.Width - 20);
        var maxHeight = Math.Max(1, area.Height - 20);

        // Lower minimums first, avoiding conflicting min/max bounds at high DPI.
        window.MinWidth = Math.Min(window.MinWidth, maxWidth);
        window.MinHeight = Math.Min(window.MinHeight, maxHeight);
        window.MaxWidth = maxWidth;
        window.MaxHeight = maxHeight;
        window.Width = Math.Min(window.Width, window.MaxWidth);
        window.Height = Math.Min(window.Height, window.MaxHeight);
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + (area.Height - window.Height) / 2;
    }
}
