namespace SchoolObservatory.Core;

/// <summary>
/// Fine-grained permissions are stored per user rather than inferred from the visible role.
/// Owner is a separate, immutable attribute that cannot be delegated.
/// </summary>
[Flags]
public enum PermissionFlags : long
{
    None = 0,
    ViewSchools = 1L << 0,
    ManageSchools = 1L << 1,
    EditCensus = 1L << 2,
    ReviewCensus = 1L << 3,
    ApproveDistrict = 1L << 4,
    ApproveSchool = 1L << 5,
    ViewReports = 1L << 6,
    ViewAudit = 1L << 7,
    ManageUsers = 1L << 8
}

public sealed record PermissionChoice(PermissionFlags Flag, string Name);

public static class PermissionCatalog
{
    public const PermissionFlags All =
        PermissionFlags.ViewSchools | PermissionFlags.ManageSchools |
        PermissionFlags.EditCensus | PermissionFlags.ReviewCensus |
        PermissionFlags.ApproveDistrict | PermissionFlags.ApproveSchool |
        PermissionFlags.ViewReports | PermissionFlags.ViewAudit |
        PermissionFlags.ManageUsers;

    public static readonly IReadOnlyList<PermissionChoice> Choices =
    [
        new(PermissionFlags.ViewSchools, "عرض دليل المدارس والمؤشرات"),
        new(PermissionFlags.ManageSchools, "إضافة وتعديل المدارس"),
        new(PermissionFlags.EditCensus, "إدخال وتعديل الإحصاء"),
        new(PermissionFlags.ReviewCensus, "مراجعة الإحصاء وإعادته"),
        new(PermissionFlags.ApproveDistrict, "اعتماد إحصاء المديرية"),
        new(PermissionFlags.ApproveSchool, "إقرار إحصاء المدرسة"),
        new(PermissionFlags.ViewReports, "عرض التقارير والتصدير"),
        new(PermissionFlags.ViewAudit, "قراءة سجل العمليات"),
        new(PermissionFlags.ManageUsers, "إدارة المستخدمين غير المؤسسين")
    ];

    public static PermissionFlags DefaultForRole(string role) => role switch
    {
        Roles.Admin => All & ~PermissionFlags.ApproveSchool & ~PermissionFlags.ManageUsers,
        Roles.Statistician => PermissionFlags.ViewSchools | PermissionFlags.ManageSchools |
            PermissionFlags.EditCensus | PermissionFlags.ReviewCensus |
            PermissionFlags.ApproveDistrict | PermissionFlags.ViewReports | PermissionFlags.ViewAudit,
        Roles.Reviewer => PermissionFlags.ViewSchools | PermissionFlags.ReviewCensus |
            PermissionFlags.ViewReports | PermissionFlags.ViewAudit,
        Roles.Principal => PermissionFlags.ViewSchools | PermissionFlags.EditCensus |
            PermissionFlags.ApproveSchool | PermissionFlags.ViewReports,
        Roles.Entry => PermissionFlags.ViewSchools | PermissionFlags.EditCensus,
        Roles.Viewer => PermissionFlags.ViewSchools | PermissionFlags.ViewReports,
        _ => throw new ArgumentException("دور المستخدم غير معتمد.", nameof(role))
    };

    public static bool IsValid(long mask) =>
        mask > 0 && (mask & ~(long)All) == 0 &&
        (mask & (long)PermissionFlags.ViewSchools) != 0;

    public static bool IsRoleAllowed(string role, long mask)
    {
        if (!Roles.All.Contains(role) || !IsValid(mask)) return false;
        var flags = (PermissionFlags)mask;
        return role switch
        {
            Roles.Principal => (flags & ~(PermissionFlags.ViewSchools | PermissionFlags.EditCensus |
                PermissionFlags.ApproveSchool | PermissionFlags.ViewReports)) == 0,
            Roles.Entry => (flags & ~(PermissionFlags.ViewSchools | PermissionFlags.EditCensus |
                PermissionFlags.ViewReports)) == 0,
            Roles.Admin or Roles.Statistician or Roles.Reviewer or Roles.Viewer =>
                !flags.HasFlag(PermissionFlags.ApproveSchool),
            _ => false
        };
    }
}
