using SchoolObservatory.Core;
using SchoolObservatory.Desktop.Services;

namespace SchoolObservatory.Desktop.ViewModels;

/// <summary>Async read model. No database query or long operation executes on the WPF UI thread.</summary>
public sealed class DashboardViewModel : ObservableViewModel
{
    private readonly DashboardService _service;
    private IReadOnlyList<UiMetric> _operational = Array.Empty<UiMetric>();
    private IReadOnlyList<UiMetric> _approvals = Array.Empty<UiMetric>();
    private bool _isLoading;
    private string _loadingMessage = "";
    private string _errorMessage = "";
    private string _staffDistribution = "";
    private string _warning = "";

    public DashboardViewModel(DashboardService service)
    {
        _service = service;
        Heading = "مرصد مدارس " + service.DistrictName;
        AcademicYear = "العام الدراسي: " + service.AcademicYear;
        CanShowReports = service.CanOpenReports;
        CanShowReconciliation = service.CanOpenReconciliation;
    }

    public string Heading { get; }
    public string AcademicYear { get; }
    public bool CanShowReports { get; }
    public bool CanShowReconciliation { get; }

    public IReadOnlyList<UiMetric> Operational
    {
        get => _operational;
        private set => SetValue(ref _operational, value);
    }
    public IReadOnlyList<UiMetric> Approvals
    {
        get => _approvals;
        private set => SetValue(ref _approvals, value);
    }
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetValue(ref _isLoading, value);
    }
    public string LoadingMessage
    {
        get => _loadingMessage;
        private set => SetValue(ref _loadingMessage, value);
    }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetValue(ref _errorMessage, value);
    }
    public string StaffDistribution
    {
        get => _staffDistribution;
        private set
        {
            if (SetValue(ref _staffDistribution, value))
                PropertyChangedForComputed();
        }
    }
    public string Warning
    {
        get => _warning;
        private set
        {
            if (SetValue(ref _warning, value))
                PropertyChangedForComputed();
        }
    }

    // These are simple derived flags; report them by raising a dependent notification
    // from the public base via independent observable backing fields.
    private bool _hasStaffDistribution;
    private bool _hasWarning;
    public bool HasStaffDistribution
    {
        get => _hasStaffDistribution;
        private set => SetValue(ref _hasStaffDistribution, value);
    }
    public bool HasWarning
    {
        get => _hasWarning;
        private set => SetValue(ref _hasWarning, value);
    }
    private void PropertyChangedForComputed()
    {
        HasStaffDistribution = StaffDistribution.Length > 0;
        HasWarning = Warning.Length > 0;
    }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        LoadingMessage = "جار تحميل المؤشرات من السجلات المحلية...";
        ErrorMessage = "";
        try
        {
            var snapshot = await Task.Run(() => _service.GetOverview());
            Operational = snapshot.Operational;
            Approvals = snapshot.Approvals;
            StaffDistribution = snapshot.StaffDistribution;
            Warning = snapshot.Warning;
        }
        catch (Exception)
        {
            ErrorMessage = "تعذر تحميل المؤشرات. راجع صلاحيات الحساب وقاعدة البيانات، ثم أعد المحاولة.";
        }
        finally
        {
            LoadingMessage = "";
            IsLoading = false;
        }
    }
}
