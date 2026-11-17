namespace SchoolObservatory.Core;

/// <summary>UI-only projection: operational records and official submissions stay distinct.</summary>
public sealed record UiMetric(string Key, string Caption, int Value);

public sealed record DashboardOverview(
    IReadOnlyList<UiMetric> Operational, IReadOnlyList<UiMetric> Approvals,
    string StaffDistribution, string Warning)
{
    public static DashboardOverview Compose(LiveDirectoryTotals live, DashboardStats official)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(official);
        var current = new List<UiMetric>
        {
            new("active_schools", "المدارس النشطة", live.Schools),
            new("council_members", "مجالس الآباء والأمهات", live.CouncilMembers)
        };
        if (live.StudentsVisible)
        {
            current.Add(new("active_students", "الطلاب النشطون", live.ActiveStudents));
            current.Add(new("registered_sections", "الشعب الدراسية المسجلة", live.Sections));
        }
        if (live.StaffVisible)
        {
            current.Add(new("active_teachers", "المعلمون في الكشوف", live.ActiveTeachers));
            current.Add(new("active_administrators", "الإداريون في الكشوف", live.ActiveAdministrators));
            current.Add(new("active_specialists", "الأخصائيون الاجتماعيون", live.ActiveSpecialists));
        }
        var officialCounts = new List<UiMetric>
        {
            new("approved_submissions", "الاستمارات المعتمدة", official.Approved),
            new("under_review_submissions", "الاستمارات قيد المراجعة", official.Submitted)
        };
        var distribution = live.StaffVisible
            ? "مباشر: " + live.DirectStaff.ToString("N0") +
              "   |   منتدب/مكلف/معار: " + live.SecondedStaff.ToString("N0") +
              "   |   إجازة أو غير محدد: " + live.UnclassifiedStaff.ToString("N0")
            : "";
        var warning = live.StaffVisible && live.DuplicateEmployeeCodes > 0
            ? "تنبيه جودة البيانات: " + live.DuplicateEmployeeCodes.ToString("N0") +
              " أرقام وظيفية مرتبطة بأكثر من مدرسة؛ صححها من ملفات الكادر."
            : "";
        return new DashboardOverview(current, officialCounts, distribution, warning);
    }
}

/// <summary>Non-sensitive window-scoped navigation context only; never stores credentials.</summary>
public sealed class NavigationContext
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 80 ||
            key.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
            new[] { "password", "secret", "token", "credential", "passwd" }
                .Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("مفتاح سياق التنقل غير مسموح.", nameof(key));
        if (value is { Length: > 300 })
            throw new ArgumentException("نص البحث طويل جدا.", nameof(value));
        _values[key] = value ?? "";
    }

    public string Get(string key) => _values.GetValueOrDefault(key, "");
    public void Clear(string key) => _values.Remove(key);
}

/// <summary>Pure logical-pixel contract; WPF measures work area in device independent units.</summary>
public static class ResponsiveSizing
{
    public static bool ShouldCollapseSidebar(double availableDipWidth) =>
        !double.IsFinite(availableDipWidth) || availableDipWidth < 760;

    public static double SidebarWidth(double availableDipWidth) =>
        ShouldCollapseSidebar(availableDipWidth) ? 0 :
        Math.Clamp(availableDipWidth * 0.20, 185, 236);
}

public sealed record UiValidationIssue(string Field, string Message);

/// <summary>Mirrors the basic school input contract, leaving authority to SqliteStore.</summary>
public static class SchoolFormValidator
{
    public static IReadOnlyList<UiValidationIssue> Validate(string? code, string? name,
        string? zone, string? ownership, string? stage, string? gender)
    {
        var issues = new List<UiValidationIssue>();
        Check("code", code, 32, "رمز المدرسة");
        Check("name", name, 180, "اسم المدرسة");
        Check("zone", zone, 100, "المنطقة");
        if (ownership is not ("حكومية" or "أهلية"))
            issues.Add(new("ownership", "اختر نوع الملكية."));
        if (stage is not ("روضة" or "أساسي" or "ثانوي" or "أساسي وثانوي" or "مجمع تربوي"))
            issues.Add(new("stage", "اختر مرحلة تعليمية صحيحة."));
        if (gender is not ("ذكور" or "إناث" or "مختلطة"))
            issues.Add(new("gender", "اختر نوع المدرسة."));
        return issues;

        void Check(string key, string? input, int max, string display)
        {
            if (string.IsNullOrWhiteSpace(input))
                issues.Add(new(key, "حقل " + display + " مطلوب."));
            else if (input.Length > max)
                issues.Add(new(key, "حقل " + display + " يتجاوز " + max + " حرفا."));
        }
    }
}
