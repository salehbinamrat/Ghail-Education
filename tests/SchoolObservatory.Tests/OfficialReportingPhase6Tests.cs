using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System.Xml.Linq;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

/// <summary>Phase 6 regression suite, committed first so broken requirements are visible.</summary>
public sealed class OfficialReportingPhase6Tests
{
    private static (SqliteStore Db, UserAccount Owner, School A, School B) Setup()
    {
        var path = Path.Combine(Path.GetTempPath(), "phase6-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteStore(path);
        store.Initialize();
        store.InitializeAdministrator("غيل بن يمين", "2026/2027", "reportowner",
            "Long-Report-Founder-Password-2026!");
        var owner = store.Authenticate("reportowner", "Long-Report-Founder-Password-2026!")!;
        store.AddSchool(owner, "A01", "مدرسة العليب", "العليب", "حكومية", "أساسي", "ذكور");
        store.AddSchool(owner, "B01", "مدرسة الريدة", "الريدة", "حكومية", "مجمع تربوي", "مختلطة");
        var schools = store.GetSchools(owner);
        return (store, owner, schools.Single(s => s.Code == "A01"),
            schools.Single(s => s.Code == "B01"));
    }

    private static ReportTable Run(SqliteStore db, UserAccount actor, ReportKind kind,
        ReportFilter? filter = null) =>
        new ReportQueryService(db).Generate(actor, new ReportRequest(kind,
            filter ?? new ReportFilter()));

    [Fact]
    public void EnrollmentSummaryMatchesAuthoritativeSchoolRostersAfterFiltering()
    {
        var (db, owner, a, b) = Setup();
        db.AddSchoolGrade(owner, a.Id, 1, "أ");
        db.AddSchoolGrade(owner, b.Id, 1, "أ");
        db.SaveStudent(owner, new SchoolStudent("", a.Id, "", "سالم", 1, "أ", "",
            "ولي أمر", "777000123", "", true, "2026/2027"));
        db.SaveStudent(owner, new SchoolStudent("", b.Id, "", "مريم", 1, "أ", "إناث",
            "ولي أمر", "777000124", "", true, "2026/2027"));
        db.SaveStudent(owner, new SchoolStudent("", b.Id, "", "أحمد", 1, "أ", "ذكور",
            "ولي أمر", "777000125", "", true, "2026/2027"));
        var aReport = Run(db, owner, ReportKind.StudentEnrollment,
            new ReportFilter(SchoolId: a.Id));
        Assert.Equal(1, aReport.Totals["students"]);
        Assert.Equal(1, aReport.RecordCount);
        Assert.DoesNotContain(aReport.Rows, r => r.Cells.Any(c => c.Contains("مريم")));
        var girls = Run(db, owner, ReportKind.StudentEnrollment,
            new ReportFilter(SchoolId: b.Id, Gender: "إناث", Grade: 1, Section: "أ"));
        Assert.Equal(1, girls.Totals["students"]);
        Assert.Single(girls.Rows);
        Assert.DoesNotContain(girls.Columns, c => c == "رقم هاتف ولي الأمر");
        Assert.Equal(3, db.GetStudents(owner, a.Id).Count + db.GetStudents(owner, b.Id).Count);
    }

    [Fact]
    public void ApprovalsNeverPromoteDraftOrSubmittedCountsToOfficial()
    {
        var (db, owner, a, _) = Setup();
        db.SaveDraft(owner, a.Id,
            new CensusData([new EnrollmentLine("1", 7, 0)], 2, 0, 1, "مسودة"));
        var pre = Run(db, owner, ReportKind.ApprovedCensus);
        Assert.Empty(pre.Rows);
        Assert.False(pre.IsApprovedSource);
        var draft = Run(db, owner, ReportKind.ApprovedCensus,
            new ReportFilter(ApprovalStatus: Statuses.Draft));
        Assert.False(draft.IsApprovedSource);
        Assert.Contains("غير معتمد", draft.StatusLabel);
        db.CreateUser(owner, "principal6", "Strong-Principal-Password-123!",
            Roles.Principal, a.Id);
        var principal = db.Authenticate("principal6", "Strong-Principal-Password-123!")!;
        var item = db.GetLatestSubmission(owner, a.Id)!;
        db.ChangeStatus(owner, item.Id, "submit");
        db.ChangeStatus(principal, item.Id, "school_approve");
        db.ChangeStatus(owner, item.Id, "district_approve");
        var approved = Run(db, owner, ReportKind.ApprovedCensus);
        Assert.True(approved.IsApprovedSource);
        Assert.False(approved.IsGovernmentTemplateApproved);
        Assert.Equal(7, approved.Totals["students"]);
        Assert.Equal(db.GetLatestSubmission(owner, a.Id)!.Data.Students, approved.Totals["students"]);
        Assert.Single(approved.Rows);
    }

    [Fact]
    public void ReportPermissionsProtectPersonalDataAndCrossSchoolScope()
    {
        var (db, owner, a, b) = Setup();
        db.AddSchoolGrade(owner, a.Id, 1, "أ");
        db.SaveStudent(owner, new SchoolStudent("", a.Id, "", "طالب خاص", 1, "أ",
            "", "ولي الأمر", "777000000", "", true, "2026/2027"));
        db.CreateUser(owner, "reportview", "Secure-Reader-Password-123!", Roles.Entry, a.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewReports |
                   PermissionFlags.ViewStudents | PermissionFlags.ViewStaff));
        var viewer = db.Authenticate("reportview", "Secure-Reader-Password-123!")!;
        Assert.Single(Run(db, viewer, ReportKind.SchoolDirectory).Rows);
        Assert.Throws<UnauthorizedAccessException>(() =>
            Run(db, viewer, ReportKind.StudentResults));
        Assert.Throws<UnauthorizedAccessException>(() =>
            Run(db, viewer, ReportKind.Attendance));
        Assert.Throws<UnauthorizedAccessException>(() =>
            Run(db, viewer, ReportKind.SchoolDirectory, new ReportFilter(SchoolId: b.Id)));
        Assert.Throws<UnauthorizedAccessException>(() =>
            Run(db, viewer, ReportKind.ParentCouncil));
        Assert.Throws<UnauthorizedAccessException>(() =>
            Run(db, viewer, ReportKind.StudentEnrollment, new ReportFilter(SchoolId: b.Id)));
        Assert.Single(Run(db, viewer, ReportKind.StudentEnrollment).Rows);
    }

    [Fact]
    public void StaffDistributionMatchesCanonicalDirectoryAndSecondmentHasOneIdentity()
    {
        var (db, owner, a, b) = Setup();
        db.SaveSchoolStaff(owner, new SchoolStaff("", a.Id, "T-90", "المعلم خالد",
            "معلم", "معلم", "رياضيات", "بكالوريوس", "ذكور", true));
        var employee = Assert.Single(db.GetSchoolStaff(owner, a.Id));
        db.SaveStaffPersonnel(owner, a.Id, employee.Id,
            new StaffPersonnelDetails("موظف", 2017, null, null,
                "منتدب", b.Name, 2024, "", ""));
        var report = Run(db, owner, ReportKind.StaffDistribution,
            new ReportFilter(StaffCategory: "معلم", AssignmentStatus: "منتدب"));
        Assert.Equal(db.GetDistrictStaffDirectory(owner).Count, report.RecordCount);
        Assert.Equal(1, report.Totals["staff"]);
        Assert.Single(report.Rows);
        var assignments = Run(db, owner, ReportKind.Secondments);
        Assert.Single(assignments.Rows);
        Assert.Equal(1, assignments.Totals["staff"]);
    }

    [Fact]
    public void HistoricalSchoolRoosterIsYearSpecificAndNeverRelabelsLiveData()
    {
        var (db, owner, a, _) = Setup();
        db.AddSchoolGrade(owner, a.Id, 1, "أ");
        db.SaveStudent(owner, new SchoolStudent("", a.Id, "", "طالب تاريخي", 1, "أ",
            "", "ولي", "777000000", "", true, "2026/2027"));
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        var historical = Run(db, owner, ReportKind.StudentEnrollment,
            new ReportFilter(SchoolId: a.Id, AcademicYear: "2026/2027"));
        Assert.Equal(1, historical.Totals["students"]);
        Assert.Throws<ArgumentException>(() =>
            Run(db, owner, ReportKind.StaffDistribution,
                new ReportFilter(AcademicYear: "2026/2027")));
    }

    [Fact]
    public void XlsxHasArabicRtlMetadataSafeLiteralTextAndNoExecutableFormulas()
    {
        var table = new ReportTable(
            ReportKind.SchoolDirectory, "دليل المدارس", "إدارة التربية والتعليم بغيل بن يمين",
            "2026/2027", DateTimeOffset.Parse("2026-11-18T08:30:00+03:00"),
            "بيانات تشغيلية - قالب مبدئي", false, false,
            ["المدرسة", "الرمز"],
            [new ReportRow(["=HYPERLINK(\"https://evil.example\")", "01"]),
             new ReportRow(["مدرسة العليب", "+CMD"])],
            new Dictionary<string, long> { ["rows"] = 2 }, ["المنطقة: العليب"]);
        using var buffer = new MemoryStream();
        XlsxReportWriter.Write(buffer, table);
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
        Assert.NotNull(zip.GetEntry("xl/styles.xml"));
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(sheet);
        using var stream = sheet!.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal("1", (string?)doc.Descendants(ns + "sheetView").First().Attribute("rightToLeft"));
        Assert.Empty(doc.Descendants(ns + "f"));
        Assert.Contains(doc.Descendants(ns + "t"), x => x.Value.Contains("HYPERLINK"));
        Assert.Contains(doc.Descendants(ns + "t"), x => x.Value.Contains("العليب"));
        Assert.Equal(2, table.RecordCount);
        Assert.Equal("2026/2027", table.AcademicYear);
    }

    [Fact]
    public void AllElevenReportKindsReturnAProvisionalTemplateWithoutClaimingGovernmentApproval()
    {
        var (db, owner, _, _) = Setup();
        foreach (var kind in Enum.GetValues<ReportKind>())
        {
            var report = Run(db, owner, kind);
            Assert.False(report.IsGovernmentTemplateApproved);
            Assert.NotEmpty(report.Columns);
            Assert.NotEmpty(report.Title);
            Assert.NotEmpty(report.StatusLabel);
            Assert.True(report.RecordCount >= 0);
        }
    }

    [Fact]
    public void StandardOpenXmlReaderAcceptsFormattedArabicWorkbookWithoutSchemaErrors()
    {
        var report = new ReportTable(ReportKind.SchoolDirectory, "دليل المدارس",
            "إدارة التربية والتعليم بمديرية غيل بن يمين", "2026/2027",
            DateTimeOffset.Parse("2026-11-18T07:00:00+03:00"),
            "بيانات تشغيلية - غير معتمد", false, false,
            ["المدرسة", "الرمز", "العدد"],
            [new ReportRow(["مدرسة العليب", "001", "20"]),
             new ReportRow(["مدرسة الريدة", "=SUM(4,5)", "18"])],
            new Dictionary<string, long> { ["students"] = 38 },
            ["المنطقة: العليب"]);
        using var stream = new MemoryStream();
        XlsxReportWriter.Write(stream, report);
        stream.Position = 0;
        using var workbook = SpreadsheetDocument.Open(stream, false);
        Assert.NotNull(workbook.WorkbookPart);
        var sheet = Assert.Single(workbook.WorkbookPart.WorksheetParts);
        var view = Assert.Single(sheet.Worksheet
            .Descendants<DocumentFormat.OpenXml.Spreadsheet.SheetView>());
        Assert.True(view.RightToLeft?.Value);
        var errors = new OpenXmlValidator().Validate(workbook).ToArray();
        Assert.True(errors.Length == 0,
            "SpreadsheetML schema violations: " +
            string.Join(" | ", errors.Take(10).Select(e => e.Description)));
    }
}
