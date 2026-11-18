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
