using System.Windows;

namespace SchoolObservatory.Desktop;

/// <summary>Keep dialogs usable at 100%, 125%, and 150% Windows display scaling.</summary>
internal static class WindowLayout
{
    public static void FitToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;

        window.MaxWidth = Math.Max(320, area.Width - 20);
        window.MaxHeight = Math.Max(300, area.Height - 20);

        // On exceptionally small screens, also lower the configured minimums.
        window.MinWidth = Math.Min(window.MinWidth, window.MaxWidth);
        window.MinHeight = Math.Min(window.MinHeight, window.MaxHeight);
        window.Width = Math.Min(window.Width, window.MaxWidth);
        window.Height = Math.Min(window.Height, window.MaxHeight);
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + (area.Height - window.Height) / 2;
    }
}
