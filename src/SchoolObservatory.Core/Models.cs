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

public sealed record SchoolInfrastructure(string District, string Subdistrict, string Village,
    string AddressDetails, double? Latitude, double? Longitude, string BuildingType,
    string BuildingCondition, int? ClassroomsCount, string OperatingShifts);
public sealed record SchoolFacility(string FacilityType, int Quantity, string Notes);
public sealed record PhysicalSectionRoom(string GradeId, int Grade, string Section,
    string? RoomCode, string? Building);
public sealed record EmployeeAssignment(string Id, string PersonId, string PayrollSchoolId,
    string WorkplaceSchoolId, string AssignmentType, DateOnly StartDate,
    DateOnly? EndDate, string Notes);

public sealed record AcademicYearRecord(string Year, string Status, bool IsActive);
public sealed record StudentPlacementHistory(string AcademicYear, string SchoolId,
    int Grade, string Section, string Action, string StudentRecordId);

public sealed record UserAccount(string Id, string Username, string Role, string? SchoolId,
    bool IsActive, long PermissionMask = 0, bool IsOwner = false, long AuthVersion = 0);
public sealed record School(string Id, string Code, string Name, string Zone, string Ownership, string Stage,
    bool IsActive, string Gender = "مختلطة");

/// <summary>Teacher, administration employee, or school social worker.</summary>
public sealed record SchoolStaff(string Id, string SchoolId, string EmployeeCode, string FullName,
    string Category, string JobTitle, string Specialization, string Qualification, string Gender, bool IsActive);

/// <summary>Extended school directory information, stored separately from legacy school identity.</summary>
public sealed record SchoolDirectoryDetails(string PrincipalName, string ContactPhone,
    string Address, int? FoundedYear, string Notes);

/// <summary>
/// Staff membership is the school in SchoolStaff.SchoolId; the actual duty location may
/// differ when a teacher or administrator is seconded, assigned or on loan.
/// Unfilled legacy records remain explicitly "غير محدد", never inferred as permanent.
/// </summary>
public sealed record StaffPersonnelDetails(string EmploymentType, int? EmploymentYear,
    int? ContractYear, int? QualificationYear, string AssignmentStatus,
    string ActualWorkplace, int? AssignmentYear, string ContactPhone, string Notes);
/// <summary>Grade and section within one school and one academic year.</summary>
public sealed record SchoolGrade(string Id, string SchoolId, string AcademicYear, int Grade, string Section,
    string Stage = "")
{
    public string DisplayName => $"{GradeNames.ForStage(Stage, Grade)} — الشعبة {Section}";
}
/// <summary>Father/mother council member belongs to exactly one school; archived records remain available.</summary>
public sealed record ParentCouncilMember(string Id, string SchoolId, string CouncilType,
    string FullName, string Position, string Phone, string Notes, bool IsActive);

/// <summary>Current operational counts, separate from the approved official census snapshot.</summary>
public sealed record LiveDirectoryTotals(int Schools, int ActiveStudents, int ActiveTeachers,
    int ActiveAdministrators, int ActiveSpecialists, int Sections, int CouncilMembers,
    bool StudentsVisible, bool StaffVisible, int DirectStaff = 0,
    int SecondedStaff = 0, int UnclassifiedStaff = 0, int DuplicateEmployeeCodes = 0);

/// <summary>A single staff identity joined with its payroll school and actual work assignment.</summary>
public sealed record DistrictStaffDirectoryRow(string StaffId, string SchoolId,
    string SchoolName, string EmployeeCode, string FullName, string Category,
    string JobTitle, string Qualification, string EmploymentType,
    string AssignmentStatus, string EffectiveWorkplace, bool IsActive)
{
    public string ActiveLabel => IsActive ? "مسجل" : "مؤرشف";
}

/// <summary>Legacy data quality warning: same payroll identifier is recorded in several schools.</summary>
public sealed record StaffCodeConflict(string EmployeeCode, int Schools);

public sealed record SchoolStudent(string Id, string SchoolId, string StudentCode, string FullName,
    int Grade, string Section, string Gender, string GuardianName, string GuardianPhone,
    string Notes, bool IsActive, string AcademicYear);
public sealed record StudentResult(string Id, string StudentId, string AcademicYear,
    string Term, string Subject, int Score, int Maximum, string Notes);
public sealed record StudentAssessment(string Id, string StudentId, string AcademicYear,
    string Category, string Rating, string Notes, DateTimeOffset RecordedAt);
public sealed record StudentAttendance(string Id, string StudentId, string Date, string Status, string Notes);

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
    public static bool Has(UserAccount actor, PermissionFlags permission) =>
        actor.IsActive && (actor.IsOwner || (((PermissionFlags)actor.PermissionMask & permission) == permission));

    public static bool CanSeeSchool(UserAccount actor, string schoolId) =>
        Has(actor, PermissionFlags.ViewSchools) &&
        (actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId == schoolId : true);

    public static bool CanEditSchool(UserAccount actor) =>
        actor.Role is Roles.Admin or Roles.Statistician &&
        Has(actor, PermissionFlags.ManageSchools);

    public static bool CanEditCensus(UserAccount actor, string schoolId) =>
        actor.Role is Roles.Admin or Roles.Statistician or Roles.Entry or Roles.Principal &&
        Has(actor, PermissionFlags.EditCensus) &&
        (actor.Role is Roles.Entry or Roles.Principal ? actor.SchoolId == schoolId : true);

    public static bool CanSeeStaff(UserAccount actor, string schoolId) =>
        CanSeeSchool(actor, schoolId) && Has(actor, PermissionFlags.ViewStaff);

    public static bool CanEditStaff(UserAccount actor, string schoolId) =>
        CanSeeSchool(actor, schoolId) && Has(actor, PermissionFlags.ManageStaff) &&
         (actor.Role is Roles.Admin or Roles.Statistician or Roles.Principal or Roles.Entry);

    public static bool CanEditSchoolProfile(UserAccount actor, string schoolId) =>
        CanSeeSchool(actor, schoolId) && Has(actor, PermissionFlags.ManageSchoolProfile) &&
         (actor.Role is Roles.Admin or Roles.Statistician or Roles.Principal or Roles.Entry);

    public static bool CanSeeStudents(UserAccount actor, string schoolId) =>
        CanSeeSchool(actor, schoolId) && Has(actor, PermissionFlags.ViewStudents);

    public static bool CanManageStudents(UserAccount actor, string schoolId) =>
        CanSeeStudents(actor, schoolId) && Has(actor, PermissionFlags.ManageStudents) &&
        actor.Role is Roles.Admin or Roles.Statistician or Roles.Principal or Roles.Entry;

    public static bool CanManageStudentEvaluation(UserAccount actor, string schoolId) =>
        CanSeeStudents(actor, schoolId) && Has(actor, PermissionFlags.ManageStudentEvaluation) &&
        actor.Role is Roles.Admin or Roles.Statistician or Roles.Principal or Roles.Entry;

    public static bool CanCreateUsers(UserAccount actor) =>
        actor.Role == Roles.Admin && Has(actor, PermissionFlags.ManageUsers);

    public static bool CanManageAccount(UserAccount actor, UserAccount target) =>
        CanCreateUsers(actor) && actor.Id != target.Id && !target.IsOwner &&
        (actor.IsOwner || (target.Role != Roles.Admin &&
            (target.PermissionMask & ~actor.PermissionMask) == 0));

    public static bool CanPerform(UserAccount actor, string action, string schoolId, string status) =>
        actor.IsActive && (action switch
        {
            "submit" => status == Statuses.Draft && CanEditCensus(actor, schoolId),
            "school_approve" => status == Statuses.Submitted &&
                actor.Role == Roles.Principal && actor.SchoolId == schoolId &&
                Has(actor, PermissionFlags.ApproveSchool),
            "district_approve" => status == Statuses.SchoolApproved &&
                actor.Role is Roles.Admin or Roles.Statistician &&
                Has(actor, PermissionFlags.ApproveDistrict),
            "return" => (status is Statuses.Submitted or Statuses.SchoolApproved) &&
                actor.Role is Roles.Admin or Roles.Statistician or Roles.Reviewer &&
                Has(actor, PermissionFlags.ReviewCensus),
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
