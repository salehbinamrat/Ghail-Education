using SchoolObservatory.Core;
using SchoolObservatory.Data;

namespace SchoolObservatory.Desktop.Services;

/// <summary>Read-only presentation query; no schema changes or duplicate metric storage.</summary>
public sealed class DashboardService
{
    private readonly SqliteStore _store;
    private readonly UserAccount _actor;

    public DashboardService(SqliteStore store, UserAccount actor)
    {
        _store = store;
        _actor = actor;
    }

    public string DistrictName => _store.DistrictName;
    public string AcademicYear => _store.AcademicYear;

    public DashboardOverview GetOverview()
    {
        var snapshot = DashboardOverview.Compose(
            _store.GetLiveDirectoryTotals(_actor), _store.GetDashboard(_actor));
        // School accounts do not receive cross-school payroll conflict diagnostics.
        return _actor.Role is Roles.Entry or Roles.Principal
            ? snapshot with { Warning = "" } : snapshot;
    }

    public bool CanOpenReports => AccessPolicy.Has(_actor, PermissionFlags.ViewReports);
    public bool CanOpenReconciliation =>
        CanOpenReports && AccessPolicy.Has(_actor, PermissionFlags.ViewSchools) &&
        AccessPolicy.Has(_actor, PermissionFlags.ViewStaff) &&
        AccessPolicy.Has(_actor, PermissionFlags.ViewStudents);
}
