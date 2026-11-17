using System.Globalization;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>
/// Report queries are permissioned, read-only and consume the application's canonical
/// store APIs. Every printed/exported document is an immutable authorized snapshot.
/// </summary>
public sealed class ReportQueryService
{
    private readonly SqliteStore _store;

    public ReportQueryService(SqliteStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    private static readonly IReadOnlyDictionary<ReportKind, string> Titles =
        new Dictionary<ReportKind, string>
        {
            [ReportKind.SchoolDirectory] = "دليل المدارس",
            [ReportKind.StudentEnrollment] = "القيد الطلابي حسب الصف والشعبة",
            [ReportKind.GradesAndSections] = "سجل الصفوف والشعب",
            [ReportKind.StudentResults] = "نتائج الطلاب",
            [ReportKind.Attendance] = "الحضور والغياب",
            [ReportKind.StaffDistribution] = "توزيع الكادر الوظيفي",
            [ReportKind.Secondments] = "الانتدابات والتكليفات الوظيفية",
            [ReportKind.ParentCouncil] = "مجالس الآباء والأمهات",
            [ReportKind.ApprovedCensus] = "ملخص الاستمارات الإحصائية",
            [ReportKind.DataInconsistencies] = "فروقات البيانات والإحصاء",
            [ReportKind.MissingInformation] = "البيانات المدرسية غير المكتملة"
        };

    public ReportTable Generate(UserAccount actor, ReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Filters);
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewSchools) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewReports))
            throw new UnauthorizedAccessException("صلاحية قراءة التقارير مطلوبة.");
        if (!Titles.ContainsKey(request.Kind))
            throw new ArgumentOutOfRangeException(nameof(request));

        var filter = request.Filters;
        var allSchools = _store.GetSchools(actor);
        if (filter.SchoolId is { Length: > 0 } school &&
            !allSchools.Any(s => s.Id == school))
            throw new UnauthorizedAccessException("المدرسة المطلوبة خارج صلاحية الحساب.");
        var schools = allSchools.Where(s =>
            (filter.SchoolId is null || s.Id == filter.SchoolId) &&
            (string.IsNullOrWhiteSpace(filter.Zone) ||
             s.Zone.Contains(filter.Zone, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(filter.Stage) || s.Stage == filter.Stage))
            .ToArray();
        var selected = schools.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var currentYear = _store.AcademicYear;
        var year = string.IsNullOrWhiteSpace(filter.AcademicYear)
            ? currentYear : filter.AcademicYear.Trim();
        if (!_store.GetAcademicYears(actor).Any(y => y.Year == year))
            throw new ArgumentException("العام الدراسي المطلوب غير موجود.");
        bool historical = year != currentYear;
        bool currentOnly = request.Kind is ReportKind.SchoolDirectory or
            ReportKind.StaffDistribution or ReportKind.Secondments or
            ReportKind.ParentCouncil or ReportKind.DataInconsistencies or
            ReportKind.MissingInformation;
        if (historical && currentOnly)
            throw new ArgumentException("هذا التقرير يعتمد بيانات تشغيلية حالية ولا يجوز تأريخه بعام سابق.");
        if (filter.Grade is < 1 or > 12)
            throw new ArgumentException("الصف المحدد غير صحيح.");
        if (filter.Gender is not null and not ("ذكور" or "إناث"))
            throw new ArgumentException("اختر جنس الطلاب من القائمة.");
        if (!string.IsNullOrEmpty(filter.ApprovalStatus) &&
            request.Kind != ReportKind.ApprovedCensus)
            throw new ArgumentException("مرشح الاعتماد مخصص للتقارير الإحصائية فقط.");
        if (filter.Section is not null &&
            request.Kind is ReportKind.ApprovedCensus or ReportKind.SchoolDirectory)
            throw new ArgumentException("هذا التقرير لا يسجل البيانات على مستوى الشعبة.");

        var rows = new List<ReportRow>();
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        IReadOnlyList<string> columns;
        bool approvedSource = false;
        void Add(params string?[] values) =>
            rows.Add(new ReportRow(values.Select(s => s ?? "").ToArray()));
        void Total(string key, long value) =>
            totals[key] = checked(totals.GetValueOrDefault(key) + value);
        void StudentAccess(School school, bool confidential = false)
        {
            if (!AccessPolicy.CanSeeStudents(actor, school.Id) ||
                (confidential && !AccessPolicy.CanManageStudents(actor, school.Id)))
                throw new UnauthorizedAccessException(
                    "تصدير السجلات الشخصية للطلاب يحتاج صلاحية إدارة سجلات المدرسة.");
        }
        void StaffAccess(School school, bool confidential = false)
        {
            if (!AccessPolicy.CanSeeStaff(actor, school.Id) ||
                (confidential && !AccessPolicy.CanEditStaff(actor, school.Id)))
                throw new UnauthorizedAccessException("صلاحية تصدير البيانات الوظيفية غير متاحة.");
        }
        IReadOnlyList<SchoolStudent> Students(School school)
        {
            StudentAccess(school);
            return historical ? _store.GetHistoricalStudents(actor, school.Id, year)
                : _store.GetStudents(actor, school.Id);
        }
        bool MatchesStudent(SchoolStudent s) =>
            (filter.Grade is null || s.Grade == filter.Grade) &&
            (string.IsNullOrWhiteSpace(filter.Section) || s.Section == filter.Section) &&
            (string.IsNullOrWhiteSpace(filter.Gender) || s.Gender == filter.Gender);
        string GradeName(School school, int number) =>
            GradeNames.ForStage(school.Stage, number);

        switch (request.Kind)
        {
            case ReportKind.SchoolDirectory:
                columns = ["رمز المدرسة", "المدرسة", "المنطقة", "المرحلة", "الملكية", "نوع المدرسة", "الحالة"];
                foreach (var s in schools.OrderBy(s => s.Name))
                    Add(s.Code, s.Name, s.Zone, s.Stage, s.Ownership, s.Gender, s.IsActive ? "نشطة" : "مؤرشفة");
                Total("schools", rows.Count);
                break;

            case ReportKind.StudentEnrollment:
                columns = ["المدرسة", "الصف", "الشعبة", "الجنس", "عدد الطلاب"];
                foreach (var school in schools)
                {
                    foreach (var group in Students(school)
                        .Where(s => MatchesStudent(s) && (historical || s.IsActive))
                        .GroupBy(s => new { s.Grade, s.Section, s.Gender })
                        .OrderBy(g => g.Key.Grade).ThenBy(g => g.Key.Section).ThenBy(g => g.Key.Gender))
                    {
                        Add(school.Name, GradeName(school, group.Key.Grade),
                            group.Key.Section, group.Key.Gender,
                            group.Count().ToString(CultureInfo.InvariantCulture));
                        Total("students", group.Count());
                    }
                }
                break;

            case ReportKind.GradesAndSections:
                columns = ["المدرسة", "العام", "المرحلة / الصف", "الشعبة", "عدد الطلاب"];
                foreach (var school in schools)
                {
                    StudentAccess(school);
                    var pupils = Students(school).Where(s => historical || s.IsActive).ToArray();
                    foreach (var g in _store.GetReportSchoolGrades(actor, school.Id, year)
                        .Where(g => (filter.Grade is null || g.Grade == filter.Grade) &&
                            (string.IsNullOrEmpty(filter.Section) || g.Section == filter.Section) &&
                            (string.IsNullOrEmpty(filter.Stage) || g.Stage == filter.Stage)))
                    {
                        var n = pupils.Count(p => p.Grade == g.Grade && p.Section == g.Section &&
                            (filter.Gender is null || p.Gender == filter.Gender));
                        Add(school.Name, year, g.DisplayName, g.Section,
                            n.ToString(CultureInfo.InvariantCulture));
                        Total("students", n);
                    }
                }
                Total("sections", rows.Count);
                break;

            case ReportKind.StudentResults:
                columns = ["المدرسة", "رمز الطالب", "اسم الطالب", "الصف", "الشعبة",
                    "الفصل", "المادة", "الدرجة", "النهاية العظمى"];
                foreach (var school in schools)
                {
                    StudentAccess(school, confidential: true);
                    var people = Students(school).Where(MatchesStudent)
                        .GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
                    foreach (var result in _store.GetReportStudentResults(actor, school.Id, year))
                    {
                        if (!people.TryGetValue(result.StudentId, out var student)) continue;
                        Add(school.Name, student.StudentCode, student.FullName,
                            GradeName(school, student.Grade), student.Section,
                            result.Term, result.Subject, result.Score.ToString(CultureInfo.InvariantCulture),
                            result.Maximum.ToString(CultureInfo.InvariantCulture));
                        Total("results", 1);
                    }
                }
                break;

            case ReportKind.Attendance:
                columns = ["المدرسة", "رمز الطالب", "اسم الطالب", "الصف", "الشعبة",
                    "التاريخ", "الحالة"];
                foreach (var school in schools)
                {
                    StudentAccess(school, confidential: true);
                    var people = Students(school).Where(MatchesStudent)
                        .GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
                    foreach (var record in _store.GetReportStudentAttendance(actor, school.Id, year))
                    {
                        if (!people.TryGetValue(record.StudentId, out var student)) continue;
                        Add(school.Name, student.StudentCode, student.FullName,
                            GradeName(school, student.Grade), student.Section,
                            record.Date, record.Status);
                        Total("attendance", 1);
                        if (record.Status == "غائب") Total("absence", 1);
                    }
                }
                break;

            case ReportKind.StaffDistribution:
            case ReportKind.Secondments:
                foreach (var school in schools)
                    StaffAccess(school, request.Kind == ReportKind.Secondments);
                var directory = _store.GetDistrictStaffDirectory(actor)
                    .Where(p => selected.Contains(p.SchoolId) && p.IsActive &&
                        (string.IsNullOrWhiteSpace(filter.StaffCategory) || p.Category == filter.StaffCategory) &&
                        (string.IsNullOrWhiteSpace(filter.EmploymentType) || p.EmploymentType == filter.EmploymentType) &&
                        (string.IsNullOrWhiteSpace(filter.AssignmentStatus) ||
                            p.AssignmentStatus == filter.AssignmentStatus))
                    .ToArray();
                if (request.Kind == ReportKind.Secondments)
                {
                    columns = ["الرقم الوظيفي", "الاسم", "المدرسة على الملاك",
                        "الفئة", "الصفة الوظيفية", "نوع التكليف", "جهة العمل"];
                    foreach (var p in directory.Where(p =>
                        p.AssignmentStatus is not ("مباشر" or "غير محدد")))
                        Add(p.EmployeeCode, p.FullName, p.SchoolName, p.Category,
                            p.EmploymentType, p.AssignmentStatus, p.EffectiveWorkplace);
                }
                else
                {
                    columns = ["المدرسة", "الفئة", "الصفة الوظيفية", "وضع العمل", "عدد الموظفين"];
                    foreach (var g in directory.GroupBy(p =>
                            new { p.SchoolName, p.Category, p.EmploymentType, p.AssignmentStatus })
                        .OrderBy(g => g.Key.SchoolName).ThenBy(g => g.Key.Category))
                        Add(g.Key.SchoolName, g.Key.Category, g.Key.EmploymentType,
                            g.Key.AssignmentStatus, g.Count().ToString(CultureInfo.InvariantCulture));
                }
                Total("staff", request.Kind == ReportKind.StaffDistribution ? directory.Length : rows.Count);
                break;

            case ReportKind.ParentCouncil:
                columns = ["المدرسة", "المجلس", "الاسم", "الصفة", "الهاتف", "الحالة"];
                foreach (var school in schools)
                {
                    if (!AccessPolicy.CanEditSchoolProfile(actor, school.Id) &&
                        !AccessPolicy.CanEditSchool(actor))
                        throw new UnauthorizedAccessException(
                            "كشف أعضاء المجلس وأرقامهم لا يتاح إلا لمن يملك صلاحية إدارة الملف.");
                    foreach (var p in _store.GetParentCouncilMembers(actor, school.Id))
                        Add(school.Name, p.CouncilType, p.FullName, p.Position,
                            p.Phone, p.IsActive ? "نشط" : "مؤرشف");
                }
                Total("members", rows.Count);
                break;

            case ReportKind.ApprovedCensus:
                columns = ["المدرسة", "العام", "المراجعة", "الحالة", "الذكور", "الإناث",
                    "إجمالي الطلاب", "المعلمون", "الفصول"];
                var statuses = new[] { Statuses.Draft, Statuses.Submitted, Statuses.SchoolApproved,
                    Statuses.Approved, Statuses.Returned };
                if (!string.IsNullOrWhiteSpace(filter.ApprovalStatus) &&
                    !statuses.Contains(filter.ApprovalStatus, StringComparer.Ordinal))
                    throw new ArgumentException("حالة اعتماد الإحصاء غير صحيحة.");
                foreach (var c in _store.GetReportCensusSubmissions(actor, year).Where(c =>
                    selected.Contains(c.SchoolId) &&
                    (filter.ApprovalStatus is null ? c.Status == Statuses.Approved :
                        c.Status == filter.ApprovalStatus)))
                {
                    // A census snapshot has grade totals, not named sections.
                    if (filter.Section is not null)
                        throw new ArgumentException("الاستمارة الرسمية لا تسجل الشعب التفصيلية.");
                    var gradeLines = filter.Grade is null ? c.Data.Rows :
                        c.Data.Rows.Where(g => g.Grade == filter.Grade.ToString()).ToArray();
                    if (filter.Grade is not null && !gradeLines.Any()) continue;
                    var boys = gradeLines.Sum(g => (long)g.Boys);
                    var girls = gradeLines.Sum(g => (long)g.Girls);
                    var school = schools.First(s => s.Id == c.SchoolId);
                    if (filter.Gender == "ذكور") girls = 0;
                    if (filter.Gender == "إناث") boys = 0;
                    Add(c.SchoolName, c.Year, c.Revision.ToString(CultureInfo.InvariantCulture),
                        c.Status == Statuses.Approved ? "معتمدة" : "غير معتمد - " + c.Status,
                        boys.ToString(CultureInfo.InvariantCulture),
                        girls.ToString(CultureInfo.InvariantCulture),
                        checked(boys + girls).ToString(CultureInfo.InvariantCulture),
                        // Grade-specific views cannot truthfully attribute teachers/classrooms per grade.
                        filter.Grade is null ? c.Data.Teachers.ToString(CultureInfo.InvariantCulture) : "غير موزع",
                        filter.Grade is null ? c.Data.Classrooms.ToString(CultureInfo.InvariantCulture) : "غير موزع");
                    Total("students", checked(boys + girls));
                    if (filter.Grade is null)
                    {
                        Total("teachers", c.Data.Teachers);
                        Total("sections", c.Data.Classrooms);
                    }
                }
                approvedSource = rows.Count > 0 &&
                    (filter.ApprovalStatus is null or Statuses.Approved);
                break;

            case ReportKind.DataInconsistencies:
                columns = ["المدرسة", "المؤشر", "التشغيلي", "المعتمد", "الفرق", "الملاحظة"];
                if (!AccessPolicy.Has(actor, PermissionFlags.ViewStudents) ||
                    !AccessPolicy.Has(actor, PermissionFlags.ViewStaff))
                    throw new UnauthorizedAccessException(
                        "مطابقة البيانات تتطلب صلاحية الاطلاع على الكادر والطلاب.");
                foreach (var diff in _store.GetCensusReconciliation(actor, year)
                    .Where(d => selected.Contains(d.SchoolId) &&
                        (d.IsDiscrepant || d.ApprovedValue is null)))
                {
                    Add(diff.SchoolName, diff.Label,
                        diff.OperationalValue.ToString(CultureInfo.InvariantCulture),
                        diff.ApprovedValue?.ToString(CultureInfo.InvariantCulture) ?? "غير متاح",
                        diff.Difference?.ToString(CultureInfo.InvariantCulture) ?? "غير متاح",
                        diff.Observation);
                }
                Total("discrepancies", rows.Count);
                break;

            case ReportKind.MissingInformation:
                columns = ["المدرسة", "رمز المدرسة", "الحقل غير المكتمل", "الإجراء المطلوب"];
                foreach (var school in schools)
                {
                    var profile = _store.GetSchoolDirectoryDetails(actor, school.Id);
                    if (string.IsNullOrWhiteSpace(school.Zone))
                        Add(school.Name, school.Code, "المنطقة", "أكمل عنوان المنطقة.");
                    if (string.IsNullOrWhiteSpace(profile.PrincipalName))
                        Add(school.Name, school.Code, "مدير المدرسة",
                            "أكمل اسم المدير في الملف التعريفي.");
                    if (string.IsNullOrWhiteSpace(profile.ContactPhone))
                        Add(school.Name, school.Code, "هاتف المدرسة",
                            "أكمل هاتف اتصال المدرسة في الملف التعريفي.");
                }
                Total("missing_fields", rows.Count);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        var applied = new List<string>();
        if (filter.SchoolId is not null)
            applied.Add("المدرسة: " + allSchools.First(s => s.Id == filter.SchoolId).Name);
        if (filter.Zone is not null) applied.Add("المنطقة: " + filter.Zone);
        if (filter.Stage is not null) applied.Add("المرحلة: " + filter.Stage);
        applied.Add("العام: " + year);
        if (filter.Grade is not null) applied.Add("الصف: " + filter.Grade);
        if (filter.Section is not null) applied.Add("الشعبة: " + filter.Section);
        if (filter.Gender is not null) applied.Add("الجنس: " + filter.Gender);
        if (filter.StaffCategory is not null) applied.Add("الفئة: " + filter.StaffCategory);
        if (filter.EmploymentType is not null) applied.Add("نوع التوظيف: " + filter.EmploymentType);
        if (filter.AssignmentStatus is not null) applied.Add("وضع العمل: " + filter.AssignmentStatus);
        if (filter.ApprovalStatus is not null) applied.Add("حالة الاعتماد: " + filter.ApprovalStatus);
        var label = request.Kind == ReportKind.ApprovedCensus
            ? (approvedSource ? "بيانات إحصائية معتمدة - قالب مبدئي غير رسمي" :
                "بيانات إحصائية غير معتمدة - ممنوع استخدامها وثيقة رسمية")
            : "بيانات تشغيلية - قالب مبدئي غير رسمي";
        var table = new ReportTable(request.Kind, Titles[request.Kind],
            "إدارة التربية والتعليم - " + _store.DistrictName,
            year, DateTimeOffset.Now, label, approvedSource, false,
            columns, rows, totals, applied);
        table.Validate();
        return table;
    }
}
