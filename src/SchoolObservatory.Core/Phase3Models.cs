namespace SchoolObservatory.Core;

/// <summary>Live counts are derived from canonical school, placement and staff records,
/// not copied into reporting tables. Staff assignments are evaluated as of today.
/// Historical staff totals are unavailable without an employment snapshot.</summary>
public sealed record SchoolOperationalSnapshot(
    string SchoolId, string SchoolName, string AcademicYear,
    int ActiveStudents, int Boys, int Girls, int Sections,
    int PayrollStaff, int WorkplaceStaff, int PayrollTeachers, int WorkplaceTeachers,
    int CouncilMembers);

/// <summary>Every visible value has an authoritative table and explicit definition.</summary>
public sealed record EducationalMetricDefinition(
    string Key, string Label, string Source, string Filters, string Formula);

/// <summary>Null approved value means no approved data exists, never a reported zero.</summary>
public sealed record CensusReconciliationRow(
    string SchoolId, string SchoolName, string AcademicYear, string MetricKey,
    string Label, int OperationalValue, int? ApprovedValue, string Source, string Formula,
    string? SubmissionId, string Observation)
{
    public int? Difference => ApprovedValue is int expected ? OperationalValue - expected : null;
    public bool IsDiscrepant => Difference is int value && value != 0;
    public string ReviewStatus => ApprovedValue is null ? "لا توجد استمارة معتمدة" :
        IsDiscrepant ? "يحتاج مراجعة" : "متطابق";
}

/// <summary>Stable catalog for reconciliation and independently computed dashboard indicators.</summary>
public static class EducationalMetricCatalog
{
    public static readonly IReadOnlyList<EducationalMetricDefinition> Definitions =
    [
        new("students.total", "الطلاب المسجلون",
            "student_history + school_students", "school_id + academic_year + active identity + active placement",
            "COUNT(DISTINCT active student IDs)"),
        new("students.boys", "الطلاب الذكور",
            "student_history + school_students", "نفس قيد الطلاب، الجنس ذكور",
            "COUNT(DISTINCT active male student IDs)"),
        new("students.girls", "الطالبات",
            "student_history + school_students", "نفس قيد الطلاب، الجنس إناث",
            "COUNT(DISTINCT active female student IDs)"),
        new("sections.total", "الشعب الدراسية المسجلة",
            "school_grades", "school_id + academic_year",
            "COUNT(registered grade/section IDs); not physical rooms"),
        new("teachers.workplace", "المعلمون العاملون فعليا",
            "school_staff + employee_assignments", "active staff, current dated assignment, not leave",
            "COUNT(DISTINCT active teacher IDs assigned to school)"),
        new("teachers.payroll", "المعلمون على ملاك المدرسة",
            "school_staff + employee_identities", "active staff + permanent payroll school",
            "COUNT(DISTINCT active teacher IDs registered on school payroll)"),
        new("staff.payroll", "إجمالي الموظفين على الملاك",
            "school_staff + employee_identities", "active staff + permanent payroll school",
            "COUNT(DISTINCT registered employee IDs)"),
        new("staff.workplace", "إجمالي الموظفين العاملين فعليا",
            "school_staff + employee_assignments", "active staff with current dated assignment, not leave",
            "COUNT(DISTINCT active employee IDs at workplace)"),
        new("councils.active", "أعضاء مجالس الآباء والأمهات",
            "school_council_members", "school_id + is_active=1",
            "COUNT(active council-member IDs)"),
        new("integrity.staff_identity", "السجلات الوظيفية غير المربوطة بهوية موحدة",
            "school_staff + employee_identities", "school_id",
            "COUNT(staff without identity or mismatched payroll school)"),
        new("integrity.staff_duplicate_code", "الأرقام الوظيفية المكررة",
            "school_staff", "cross-school case-insensitive trimmed employee codes",
            "COUNT(duplicate payroll codes involving the school)")
    ];

    public static EducationalMetricDefinition? Find(string key) =>
        Definitions.FirstOrDefault(x => x.Key == key);
}
