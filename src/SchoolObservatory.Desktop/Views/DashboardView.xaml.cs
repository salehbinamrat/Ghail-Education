using System.Windows;
using System.Windows.Controls;

namespace SchoolObservatory.Desktop.Views;

/// <summary>Navigation events only; queries and state are in DashboardViewModel.</summary>
public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    public event EventHandler? RefreshRequested;
    public event EventHandler? ReportsRequested;
    public event EventHandler? ReconciliationRequested;

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void Reports_Click(object sender, RoutedEventArgs e) =>
        ReportsRequested?.Invoke(this, EventArgs.Empty);

    private void Reconciliation_Click(object sender, RoutedEventArgs e) =>
        ReconciliationRequested?.Invoke(this, EventArgs.Empty);
}
