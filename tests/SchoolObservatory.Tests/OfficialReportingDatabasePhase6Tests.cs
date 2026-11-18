using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class OfficialReportingDatabasePhase6Tests
{
    private static (SqliteStore Store, UserAccount Owner, School SchoolA, School SchoolB) Setup()
    {
        var path = Path.Combine(Path.GetTempPath(), "phase6-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027",
            "founder", "Strong-Founder-Password-123!");
        var owner = db.Authenticate("founder", "Strong-Founder-Password-123!")!;
        db.AddSchool(owner, "P6-1", "مدرسة العليب", "العليب", "حكومية", "أساسي", "ذكور");
        db.AddSchool(owner, "P6-2", "مدرسة الريدة", "الريدة", "حكومية", "أساسي", "ذكور");
        var schools = db.GetSchools(owner);
        return (db, owner, schools.Single(x => x.Code == "P6-1"),
            schools.Single(x => x.Code == "P6-2"));
    }

    [Fact]
    public void EnrollmentExportTotalsMatchAuthoritativeRosterAndFilter()
    {
        var (db, owner, a, b) = Setup();
        db.AddSchoolGrade(owner, a.Id, 1, "أ");
        db.AddSchoolGrade(owner, a.Id, 1, "ب");
        db.AddSchoolGrade(owner, b.Id, 1, "أ");
        db.SaveStudent(owner, new SchoolStudent("", a.Id, "", "أحمد علي", 1, "أ",
            "ذكور", "", "", "", true, "2026/2027"));
        db.SaveStudent(owner, new SchoolStudent("", a.Id, "", "سالم عمر", 1, "ب",
            "ذكور", "", "", "", true, "2026/2027"));
        db.SaveStudent(owner, new SchoolStudent("", b.Id, "", "طالب آخر", 1, "أ",
            "ذكور", "", "", "", true, "2026/2027"));
        var service = new OfficialReportService(db);
        var aReport = service.Generate(owner,
            new EducationalReportFilter(EducationalReportKind.StudentEnrollment, SchoolId: a.Id));
        Assert.Equal(db.GetStudents(owner, a.Id).Count, aReport.RecordTotal);
        var gradeReport = service.Generate(owner, new EducationalReportFilter(
            EducationalReportKind.StudentEnrollment, SchoolId: a.Id, Grade: 1, Section: "أ"));
        Assert.Equal(1, gradeReport.RecordTotal);
        Assert.DoesNotContain(aReport.Rows, row => row.Values.Contains("طالب آخر"));
        Assert.False(aReport.IsOfficiallyApproved);
    }

    [Fact]
    public void ApprovedReportMatchesOfficialDatabaseSnapshotExactly()
    {
        var (db, owner, a, _) = Setup();
        db.SaveDraft(owner, a.Id, new CensusData(
            [new EnrollmentLine("1", 12, 8)], 2, 1, 3, ""));
        var submission = db.GetLatestSubmission(owner, a.Id)!;
        db.CreateUser(owner, "principal6", "Strong-Password-123!", Roles.Principal, a.Id);
        var principal = db.Authenticate("principal6", "Strong-Password-123!")!;
        db.ChangeStatus(owner, submission.Id, "submit");
        db.ChangeStatus(principal, submission.Id, "school_approve");
        db.ChangeStatus(owner, submission.Id, "district_approve");

        var official = Assert.Single(db.GetApprovedCensusReport(owner, "2026/2027"));
        var report = new OfficialReportService(db).Generate(owner,
            new EducationalReportFilter(EducationalReportKind.ApprovedCensus, SchoolId: a.Id));
        Assert.True(report.IsOfficiallyApproved);
        var row = Assert.Single(report.Rows).Values;
        Assert.Equal(official.Students.ToString(), row[4]);
        Assert.Equal(official.Teachers.ToString(), row[5]);
        Assert.Equal(official.Classrooms.ToString(), row[6]);
        Assert.Equal(20, official.Students);
        Assert.Equal(3, official.Teachers);
    }

    [Fact]
    public void SchoolUserCannotExportAnotherSchoolsStudentsOrPersonnel()
    {
        var (db, owner, a, b) = Setup();
        db.CreateUser(owner, "principal7", "Strong-Password-123!", Roles.Principal, a.Id);
        var principal = db.Authenticate("principal7", "Strong-Password-123!")!;
        var reports = new OfficialReportService(db);
        Assert.Throws<UnauthorizedAccessException>(() =>
            reports.Generate(principal, new EducationalReportFilter(
                EducationalReportKind.StudentEnrollment, SchoolId: b.Id)));
        Assert.Throws<UnauthorizedAccessException>(() =>
            reports.Generate(principal, new EducationalReportFilter(
                EducationalReportKind.StaffDistribution, SchoolId: b.Id)));
        Assert.Single(reports.Generate(principal,
            new EducationalReportFilter(EducationalReportKind.SchoolDirectory)).Rows);
    }

    [Fact]
    public void UnapprovedCensusNeverAppearsAsOfficial()
    {
        var (db, owner, a, _) = Setup();
        db.SaveDraft(owner, a.Id,
            new CensusData([new EnrollmentLine("1", 8, 9)], 1, 1, 2, ""));
        var report = new OfficialReportService(db).Generate(owner,
            new EducationalReportFilter(EducationalReportKind.ApprovedCensus));
        Assert.Equal(0, report.RecordTotal);
        Assert.False(report.IsOfficiallyApproved);
    }
    [Fact]
    public void EmptyClassSectionIsRepresentedWithZeroEnrolledStudents()
    {
        var (db, owner, school, _) = Setup();
        db.AddSchoolGrade(owner, school.Id, 3, "ج");
        var report = new OfficialReportService(db).Generate(owner,
            new EducationalReportFilter(EducationalReportKind.GradesAndSections,
                SchoolId: school.Id, Grade: 3, Section: "ج"));
        var row = Assert.Single(report.Rows).Values;
        Assert.Equal("ج", row[2]);
        Assert.Equal("0", row[4]);
    }

    [Fact]
    public void OperationalCensusStatusForPriorAcademicYearIsStillReportable()
    {
        var (db, owner, school, _) = Setup();
        db.SaveDraft(owner, school.Id,
            new CensusData([new EnrollmentLine("1", 2, 3)], 1, 1, 1, ""));
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        var report = new OfficialReportService(db).Generate(owner,
            new EducationalReportFilter(EducationalReportKind.CensusSubmissions,
                SchoolId: school.Id, AcademicYear: "2026/2027", CensusStatus: Statuses.Draft));
        var row = Assert.Single(report.Rows).Values;
        Assert.Equal(Statuses.Draft, row[3]);
        Assert.Equal("5", row[6]);
        Assert.False(report.IsOfficiallyApproved);
    }

}
