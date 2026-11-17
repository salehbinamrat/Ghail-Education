using System.Collections.ObjectModel;

namespace SchoolObservatory.Core;

/// <summary>Eleven deliberately separate reporting subjects. No table is silently relabeled official.</summary>
public enum ReportKind
{
    SchoolDirectory, StudentEnrollment, GradesAndSections, StudentResults,
    Attendance, StaffDistribution, Secondments, ParentCouncil,
    ApprovedCensus, DataInconsistencies, MissingInformation
}

/// <summary>All filters are optional; omitted year means the active year, never an invented year.</summary>
public sealed record ReportFilter(
    string? SchoolId = null, string? Zone = null, string? AcademicYear = null,
    string? Stage = null, int? Grade = null, string? Section = null,
    string? Gender = null, string? StaffCategory = null,
    string? EmploymentType = null, string? AssignmentStatus = null,
    string? ApprovalStatus = null);

public sealed record ReportRequest(ReportKind Kind, ReportFilter Filters);

public sealed record ReportRow(IReadOnlyList<string> Cells);

/// <summary>A single immutable, authorization-checked snapshot used for preview, PDF and XLSX.</summary>
public sealed record ReportTable(
    ReportKind Kind, string Title, string Institution, string AcademicYear,
    DateTimeOffset GeneratedAt, string StatusLabel,
    bool IsApprovedSource, bool IsGovernmentTemplateApproved,
    IReadOnlyList<string> Columns, IReadOnlyList<ReportRow> Rows,
    IReadOnlyDictionary<string, long> Totals, IReadOnlyList<string> AppliedFilters)
{
    public int RecordCount => Rows.Count;

    public void Validate()
    {
        if (Columns.Count == 0 || Columns.Count > 24)
            throw new ArgumentException("عدد أعمدة التقرير غير صالح.");
        if (Rows.Count > 100_000)
            throw new ArgumentException("حجم التقرير يتجاوز 100000 صف؛ اختر نطاقا أصغر.");
        if (Rows.Any(row => row.Cells.Count != Columns.Count))
            throw new ArgumentException("بعض الصفوف لا تطابق أعمدة التقرير.");
        if (Kind != ReportKind.ApprovedCensus && IsApprovedSource)
            throw new InvalidOperationException("التقرير التشغيلي لا يمكن أن يحمل تصنيف المعتمد.");
        if (IsGovernmentTemplateApproved)
            throw new InvalidOperationException("لم يتم تسجيل أي قالب حكومي معتمد في النظام.");
        if (Kind == ReportKind.ApprovedCensus && !IsApprovedSource &&
            !StatusLabel.Contains("غير معتمد", StringComparison.Ordinal))
            throw new InvalidOperationException("بيانات التعداد غير المعتمدة يجب تمييزها.");
    }
}


/// <summary>User-selected options for provisional print layouts; no claim of government approval.</summary>
public sealed record ReportPrintOptions(string HeaderNote = "", string FooterNote = "",
    bool? Landscape = null)
{
    public void Validate()
    {
        if (HeaderNote.Length > 120 || FooterNote.Length > 80 ||
            HeaderNote.Contains('\n') || FooterNote.Contains('\n'))
            throw new ArgumentException("نصوص رأس وتذييل الصفحة طويلة أو تحتوي أسطرا غير مسموحة.");
    }
}
