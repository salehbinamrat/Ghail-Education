using System.Globalization;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>All report rows are read from the existing authenticated store APIs.
/// No report creates or modifies operational records or approval decisions.</summary>
public sealed class OfficialReportService(SqliteStore store)
{
    private static readonly IReadOnlyDictionary<EducationalReportKind, string> Titles =
        new Dictionary<EducationalReportKind, string>
        {
            [EducationalReportKind.SchoolDirectory] = "دليل المدارس",
            [EducationalReportKind.StudentEnrollment] = "كشف قيد الطلاب",
            [EducationalReportKind.GradesAndSections] = "الصفوف والشعب",
            [EducationalReportKind.StudentResults] = "نتائج الطلاب",
            [EducationalReportKind.Attendance] = "الحضور والغياب",
            [EducationalReportKind.StaffDistribution] = "توزيع الكادر",
            [EducationalReportKind.PersonnelAssignments] = "الانتدابات والتكليفات",
            [EducationalReportKind.ParentCouncils] = "أعضاء مجالس الآباء والأمهات",
            [EducationalReportKind.ApprovedCensus] = "ملخص الإحصاء المعتمد",
            [EducationalReportKind.DataQuality] = "البيانات الناقصة والتعارضات"
        };

    public EducationalReport Generate(UserAccount actor, EducationalReportFilter filter)
    {
        if (!AccessPolicy.Has(actor, PermissionFlags.ViewReports) ||
            !AccessPolicy.Has(actor, PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException("لا تملك صلاحية التقارير.");
        var year = filter.AcademicYear ?? store.AcademicYear;
        var schools = store.GetSchools(actor)
            .Where(s => filter.SchoolId is null || s.Id == filter.SchoolId)
            .Where(s => filter.Zone is null || s.Zone == filter.Zone)
            .Where(s => filter.Stage is null || s.Stage == filter.Stage).ToArray();
        if (filter.SchoolId is not null && schools.Length == 0)
            throw new UnauthorizedAccessException("المدرسة ليست ضمن صلاحياتك أو المرشحات.");
        var required = filter.Kind switch
        {
            EducationalReportKind.StudentEnrollment or EducationalReportKind.GradesAndSections or
                EducationalReportKind.StudentResults or EducationalReportKind.Attendance
                => PermissionFlags.ViewStudents,
            EducationalReportKind.StaffDistribution or EducationalReportKind.PersonnelAssignments
                => PermissionFlags.ViewStaff,
            EducationalReportKind.DataQuality => PermissionFlags.ViewStudents | PermissionFlags.ViewStaff,
            _ => PermissionFlags.ViewSchools
        };
        if (!AccessPolicy.Has(actor, required))
            throw new UnauthorizedAccessException("لا تملك صلاحية قراءة المعلومات المطلوبة لهذا التقرير.");

        var rows = new List<ReportRecord>();
        string[] columns;
        void Add(params string[] cells) => rows.Add(new ReportRecord(cells));
        bool MatchStudent(SchoolStudent st) =>
            (!filter.Grade.HasValue || st.Grade == filter.Grade.Value) &&
            (filter.Section is null || st.Section == filter.Section) &&
            (filter.Gender is null || st.Gender == filter.Gender);
        bool MatchStaff(DistrictStaffDirectoryRow st) =>
            (filter.StaffCategory is null || st.Category == filter.StaffCategory) &&
            (filter.EmploymentStatus is null || st.EmploymentType == filter.EmploymentStatus) &&
            (filter.AssignmentStatus is null || st.AssignmentStatus == filter.AssignmentStatus);

        switch (filter.Kind)
        {
            case EducationalReportKind.SchoolDirectory:
                columns = ["رمز المدرسة", "المدرسة", "المنطقة", "المرحلة", "الملكية", "الحالة"];
                foreach (var s in schools)
                    Add(s.Code, s.Name, s.Zone, s.Stage, s.Ownership, s.IsActive ? "نشطة" : "مؤرشفة");
                break;

            case EducationalReportKind.ApprovedCensus:
                columns = ["المدرسة", "العام", "الذكور", "الإناث", "إجمالي الطلاب", "المعلمون", "الفصول", "الاعتماد"];
                var ids = schools.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var r in store.GetApprovedCensusReport(actor, year)
                    .Where(x => ids.Contains(x.SchoolId)))
                    Add(r.SchoolName, r.AcademicYear, r.Boys.ToString(CultureInfo.InvariantCulture),
                        r.Girls.ToString(CultureInfo.InvariantCulture),
                        r.Students.ToString(CultureInfo.InvariantCulture),
                        r.Teachers.ToString(CultureInfo.InvariantCulture),
                        r.Classrooms.ToString(CultureInfo.InvariantCulture), "معتمد");
                break;

            case EducationalReportKind.StaffDistribution:
            case EducationalReportKind.PersonnelAssignments:
                columns = ["المدرسة", "الرقم الوظيفي", "الاسم", "الفئة", "المسمى", "نوع التوظيف", "وضع التكليف", "موقع العمل"];
                var allowed = schools.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var st in store.GetDistrictStaffDirectory(actor)
                    .Where(s => allowed.Contains(s.SchoolId) && MatchStaff(s)))
                {
                    if (filter.Kind == EducationalReportKind.PersonnelAssignments &&
                        st.AssignmentStatus is not ("منتدب" or "مكلف" or "معار")) continue;
                    Add(st.SchoolName, st.EmployeeCode, st.FullName, st.Category, st.JobTitle,
                        st.EmploymentType, st.AssignmentStatus, st.EffectiveWorkplace);
                }
                break;

            case EducationalReportKind.ParentCouncils:
                columns = ["المدرسة", "المجلس", "العضو", "المنصب", "الهاتف", "الحالة"];
                foreach (var s in schools)
                    foreach (var m in store.GetParentCouncilMembers(actor, s.Id))
                        Add(s.Name, m.CouncilType, m.FullName, m.Position, m.Phone,
                            m.IsActive ? "نشط" : "مؤرشف");
                break;

            case EducationalReportKind.DataQuality:
                columns = ["المدرسة", "المؤشر", "القيمة التشغيلية", "القيمة المعتمدة", "الملاحظة"];
                var scoped = schools.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var x in store.GetCensusReconciliation(actor, year)
                    .Where(x => scoped.Contains(x.SchoolId) && (x.IsDiscrepant ||
                        x.MetricKey.StartsWith("integrity.", StringComparison.Ordinal) &&
                        x.OperationalValue != 0 || x.ApprovedValue is null)))
                    Add(x.SchoolName, x.Label, x.OperationalValue.ToString(CultureInfo.InvariantCulture),
                        x.ApprovedValue?.ToString(CultureInfo.InvariantCulture) ?? "غير متوفر",
                        x.Observation);
                break;

            default:
                columns = filter.Kind switch
                {
                    EducationalReportKind.GradesAndSections =>
                        ["المدرسة", "الصف", "الشعبة", "العام", "عدد الطلاب"],
                    EducationalReportKind.StudentResults =>
                        ["المدرسة", "الطالب", "الصف", "الشعبة", "الفصل", "المادة", "الدرجة", "النهاية"],
                    EducationalReportKind.Attendance =>
                        ["المدرسة", "الطالب", "الصف", "الشعبة", "التاريخ", "الحالة", "ملاحظات"],
                    _ => ["المدرسة", "رقم الطالب", "الاسم", "الصف", "الشعبة", "الجنس", "العام"]
                };
                foreach (var s in schools)
                {
                    var students = (year == store.AcademicYear
                        ? store.GetStudents(actor, s.Id)
                        : store.GetHistoricalStudents(actor, s.Id, year))
                        .Where(MatchStudent).ToArray();
                    if (filter.Kind == EducationalReportKind.GradesAndSections)
                    {
                        foreach (var grp in students.GroupBy(st => new { st.Grade, st.Section })
                            .OrderBy(g => g.Key.Grade).ThenBy(g => g.Key.Section))
                            Add(s.Name, grp.Key.Grade.ToString(CultureInfo.InvariantCulture),
                                grp.Key.Section, year, grp.Select(x => x.Id).Distinct().Count()
                                    .ToString(CultureInfo.InvariantCulture));
                    }
                    else foreach (var st in students)
                    {
                        var grade = st.Grade.ToString(CultureInfo.InvariantCulture);
                        if (filter.Kind == EducationalReportKind.StudentEnrollment)
                            Add(s.Name, st.StudentCode, st.FullName, grade, st.Section, st.Gender, year);
                        else if (filter.Kind == EducationalReportKind.StudentResults)
                        {
                            foreach (var v in store.GetStudentResults(actor, s.Id, st.Id).Where(v => v.AcademicYear == year))
                                Add(s.Name, st.FullName, grade, st.Section, v.Term, v.Subject,
                                    v.Score.ToString(CultureInfo.InvariantCulture),
                                    v.Maximum.ToString(CultureInfo.InvariantCulture));
                        }
                        else if (filter.Kind == EducationalReportKind.Attendance)
                        {
                            foreach (var a in store.GetAttendanceForReport(actor, s.Id, year, st.Id))
                                Add(s.Name, st.FullName, grade, st.Section, a.Date, a.Status, a.Notes);
                        }
                    }
                }
                break;
        }

        if (filter.CensusStatus is not null && filter.Kind != EducationalReportKind.ApprovedCensus)
            throw new ArgumentException("مرشح الاعتماد متاح لتقرير الإحصاء المعتمد فقط.");
        if (filter.CensusStatus is not null && filter.CensusStatus != Statuses.Approved)
            rows.Clear();

        var applied = new Dictionary<string,string>();
        void Put(string title, string? value) { if (!string.IsNullOrWhiteSpace(value)) applied[title] = value; }
        Put("المدرسة", schools.Length == 1 ? schools[0].Name : filter.SchoolId);
        Put("المنطقة", filter.Zone); Put("المرحلة", filter.Stage);
        Put("الصف", filter.Grade?.ToString(CultureInfo.InvariantCulture));
        Put("الشعبة", filter.Section); Put("الجنس", filter.Gender);
        Put("فئة الكادر", filter.StaffCategory); Put("التوظيف", filter.EmploymentStatus);
        Put("التكليف", filter.AssignmentStatus); Put("اعتماد الإحصاء", filter.CensusStatus);
        var document = new EducationalReport(Titles[filter.Kind],
            schools.Length == 1 ? schools[0].Name : "إدارة التربية والتعليم - مديرية غيل بن يمين",
            year, filter.Kind == EducationalReportKind.ApprovedCensus && rows.Count > 0,
            columns, rows, applied, DateTimeOffset.Now);
        document.Validate();
        return document;
    }
}
