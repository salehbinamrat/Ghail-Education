namespace SchoolObservatory.Core;

public static class Roles
{
    public const string Admin = "admin";
    public const string Statistician = "statistician";
    public const string Reviewer = "reviewer";
    public const string Principal = "principal";
    public const string Entry = "entry";
    public const string Viewer = "viewer";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Admin, Statistician, Reviewer, Principal, Entry, Viewer };
}

public static class Statuses
{
    public const string Draft = "draft";
    public const string Submitted = "submitted";
    public const string SchoolApproved = "school_approved";
    public const string Approved = "approved";
    public const string Returned = "returned";
}

public sealed record UserAccount(string Id, string Username, string Role, string? SchoolId, bool IsActive);
public sealed record School(string Id, string Code, string Name, string Zone, string Ownership, string Stage, bool IsActive);
public sealed record EnrollmentLine(string Grade, int Boys, int Girls)
{
    public int Total => checked(Boys + Girls);
}
public sealed record CensusData(IReadOnlyList<EnrollmentLine> Rows, int MaleTeachers, int FemaleTeachers, int Classrooms, string Notes)
{
    public int Boys => Rows.Sum(r => r.Boys);
    public int Girls => Rows.Sum(r => r.Girls);
    public int Students => checked(Boys + Girls);
    public int Teachers => checked(MaleTeachers + FemaleTeachers);
}
public sealed record CensusSubmission(string Id, string SchoolId, string SchoolName, string Year, int Revision,
    string Status, CensusData Data, string? DecisionReason, DateTimeOffset CreatedUtc);
public sealed record DashboardStats(int Schools, int Submitted, int Approved, int Students, int Teachers, int Classrooms);
public sealed record AuditItem(DateTimeOffset When, string Actor, string Action, string EntityId, string Details);

public static class AccessPolicy
{
    public static bool CanSeeSchool(UserAccount actor, string schoolId) =>
        actor.IsActive && ((actor.Role is Roles.Admin or Roles.Statistician or Roles.Reviewer or Roles.Viewer) ||
        actor.SchoolId == schoolId);

    public static bool CanEditSchool(UserAccount actor) => actor.IsActive &&
        (actor.Role is Roles.Admin or Roles.Statistician);

    public static bool CanEditCensus(UserAccount actor, string schoolId) => actor.IsActive &&
        ((actor.Role is Roles.Admin or Roles.Statistician) ||
         (actor.Role is Roles.Entry or Roles.Principal) && actor.SchoolId == schoolId);

    public static bool CanCreateUsers(UserAccount actor) => actor.IsActive && actor.Role == Roles.Admin;

    public static bool CanPerform(UserAccount actor, string action, string schoolId, string status) =>
        actor.IsActive && (action switch
        {
            "submit" => status == Statuses.Draft && CanEditCensus(actor, schoolId),
            "school_approve" => status == Statuses.Submitted &&
                actor.Role == Roles.Principal && actor.SchoolId == schoolId,
            "district_approve" => status == Statuses.SchoolApproved &&
                (actor.Role is Roles.Admin or Roles.Statistician),
            "return" => (status is Statuses.Submitted or Statuses.SchoolApproved) &&
                (actor.Role is Roles.Admin or Roles.Statistician or Roles.Reviewer),
            _ => false
        });
}

public static class CensusValidation
{
    public static IReadOnlyList<string> Validate(CensusData value)
    {
        var errors = new List<string>();
        if (value.Rows.Count == 0) errors.Add("يجب إدخال صف دراسي واحد على الأقل.");
        if (value.Rows.GroupBy(x => x.Grade).Any(g => g.Count() > 1)) errors.Add("لا يمكن تكرار الصف الدراسي.");
        foreach (var row in value.Rows)
        {
            if (!int.TryParse(row.Grade, out int grade) || grade < 1 || grade > 12)
                errors.Add($"رمز الصف غير صحيح: {row.Grade}");
            if (row.Boys < 0 || row.Girls < 0) errors.Add($"أعداد سالبة في الصف: {row.Grade}");
        }
        if (value.MaleTeachers < 0 || value.FemaleTeachers < 0 || value.Classrooms < 0)
            errors.Add("لا يجوز إدخال أعداد سالبة للمعلمين أو الفصول.");
        if (value.Notes.Length > 2000) errors.Add("الملاحظات تتجاوز 2000 حرف.");
        try { _ = value.Students; _ = value.Teachers; }
        catch (OverflowException) { errors.Add("حجم الأعداد يتجاوز الحد المسموح."); }
        return errors;
    }
}
