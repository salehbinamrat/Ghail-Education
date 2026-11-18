namespace SchoolObservatory.Core;

/// <summary>Approved means a verified, final district census snapshot only.
/// Other datasets are operational and never inherit an approval badge.</summary>
public enum EducationalReportKind
{
    SchoolDirectory, StudentEnrollment, GradesAndSections, StudentResults,
    Attendance, StaffDistribution, PersonnelAssignments, ParentCouncils,
    ApprovedCensus, CensusSubmissions, DataQuality
}

public sealed record EducationalReportFilter(
    EducationalReportKind Kind,
    string? SchoolId = null,
    string? Zone = null,
    string? AcademicYear = null,
    string? Stage = null,
    int? Grade = null,
    string? Section = null,
    string? Gender = null,
    string? StaffCategory = null,
    string? EmploymentStatus = null,
    string? AssignmentStatus = null,
    string? CensusStatus = null);

public sealed record ReportRecord(IReadOnlyList<string> Values);

public sealed record EducationalReport(
    string Title, string Institution, string AcademicYear,
    bool IsOfficiallyApproved, IReadOnlyList<string> Columns,
    IReadOnlyList<ReportRecord> Rows,
    IReadOnlyDictionary<string,string> AppliedFilters,
    DateTimeOffset GeneratedAt)
{
    public int RecordTotal => Rows.Count;
    public string ApprovalLabel => IsOfficiallyApproved
        ? "إحصاء معتمد من المديرية"
        : "تقرير تشغيلي / غير معتمد رسميا";
    public void Validate()
    {
        if (Columns.Count == 0 || Columns.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("أعمدة التقرير غير صالحة.");
        if (Rows.Any(r => r.Values.Count != Columns.Count))
            throw new ArgumentException("عدد قيم الصف لا يطابق أعمدة التقرير.");
    }
}

public static class ReportAccess
{
    public static bool CanRead(UserAccount actor, string schoolId, PermissionFlags dataPermission) =>
        AccessPolicy.Has(actor, PermissionFlags.ViewReports) &&
        AccessPolicy.CanSeeSchool(actor, schoolId) &&
        AccessPolicy.Has(actor, dataPermission);
}

public static class ReportTextSafety
{
    /// <summary>Text-only. XLSX writers must explicitly set the cell data type to text.</summary>
    public static string AsSpreadsheetText(string? value)
    {
        var text = value ?? "";
        var trimmed = text.TrimStart();
        if (trimmed.Length != 0 && "=+-@".Contains(trimmed[0]))
            return "'" + text;
        return text;
    }
}


public static class ReportFilterRules
{
    public static bool StudentDimensions(EducationalReportKind kind) =>
        kind is EducationalReportKind.StudentEnrollment or
            EducationalReportKind.GradesAndSections or EducationalReportKind.StudentResults or
            EducationalReportKind.Attendance;

    public static bool StaffDimensions(EducationalReportKind kind) =>
        kind is EducationalReportKind.StaffDistribution or EducationalReportKind.PersonnelAssignments;

    public static bool CensusStatus(EducationalReportKind kind) =>
        kind is EducationalReportKind.ApprovedCensus or EducationalReportKind.CensusSubmissions;

    public static void Validate(EducationalReportFilter filter)
    {
        if (!Enum.IsDefined(filter.Kind)) throw new ArgumentException("نوع التقرير غير معتمد.");
        if (!StudentDimensions(filter.Kind) &&
            (filter.Grade is not null || filter.Section is not null || filter.Gender is not null))
            throw new ArgumentException("مرشحات الطلاب غير متاحة لهذا التقرير.");
        if (!StaffDimensions(filter.Kind) &&
            (filter.StaffCategory is not null || filter.EmploymentStatus is not null ||
             filter.AssignmentStatus is not null))
            throw new ArgumentException("مرشحات الكادر غير متاحة لهذا التقرير.");
        if (!CensusStatus(filter.Kind) && filter.CensusStatus is not null)
            throw new ArgumentException("مرشح اعتماد الإحصاء غير متاح لهذا التقرير.");
        if (filter.Grade is int grade && grade is < 1 or > 12)
            throw new ArgumentException("رقم الصف غير صحيح.");
    }
}
