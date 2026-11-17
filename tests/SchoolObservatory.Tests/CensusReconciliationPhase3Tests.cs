using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

/// <summary>Phase 3: authoritative live sources, independent official approvals, and traceable reconciliation.</summary>
public sealed class CensusReconciliationPhase3Tests
{
    private static (SqliteStore Store, UserAccount Owner, School A, School B) Setup()
    {
        var path = Path.Combine(Path.GetTempPath(), "phase3-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Strong-Founder-Password-123!");
        var owner = db.Authenticate("founder", "Strong-Founder-Password-123!")!;
        db.AddSchool(owner, "P3-01", "مدرسة العليب", "العليب", "حكومية", "أساسي", "ذكور");
        db.AddSchool(owner, "P3-02", "مدرسة الريدة", "الريدة", "حكومية", "أساسي", "ذكور");
        var schools = db.GetSchools(owner);
        return (db, owner, schools.Single(x => x.Code == "P3-01"),
            schools.Single(x => x.Code == "P3-02"));
    }

    [Fact]
    public void RosterSectionsAndCouncilsHaveOneLiveSourceAndRespondToArchiving()
    {
        var (db, owner, a, _) = Setup();
        db.AddSchoolGrade(owner, a.Id, 1, "أ");
        db.AddSchoolGrade(owner, a.Id, 1, "ب");
        db.SaveStudent(owner, new SchoolStudent("", a.Id, "", "أحمد سالم", 1, "أ",
            "", "", "", "", true, "2026/2027"));
        db.SaveStudent(owner, new SchoolStudent("", a.Id, "", "خالد سالم", 1, "ب",
            "", "", "", "", true, "2026/2027"));
        db.SaveParentCouncilMember(owner, a.Id, new ParentCouncilMember("", a.Id,
            "مجلس الآباء", "رئيس المجلس", "رئيس", "", "", true));
        var before = db.GetSchoolOperationalSnapshot(owner, a.Id, "2026/2027");
        Assert.Equal(2, before.ActiveStudents);
        Assert.Equal(2, before.Sections);
        Assert.Equal(1, before.CouncilMembers);
        var student = db.GetStudents(owner, a.Id).First();
        db.SaveStudent(owner, student with { IsActive = false });
        Assert.Equal(1, db.GetSchoolOperationalSnapshot(owner, a.Id, "2026/2027").ActiveStudents);
        db.SaveStudent(owner, student with { IsActive = true });
        Assert.Equal(2, db.GetSchoolOperationalSnapshot(owner, a.Id, "2026/2027").ActiveStudents);
        db.Initialize(); // idempotent migration must never duplicate historical placement
        Assert.Equal(2, db.GetSchoolOperationalSnapshot(owner, a.Id, "2026/2027").ActiveStudents);
    }

    [Fact]
    public void WorkplaceCountsDifferFromPayrollCountsWithoutCreatingAnotherPerson()
    {
        var (db, owner, a, b) = Setup();
        db.SaveSchoolStaff(owner, new SchoolStaff("", a.Id, "T-001", "معلم منتدب",
            "معلم", "معلم", "الرياضيات", "بكالوريوس", "ذكور", true));
        var employee = Assert.Single(db.GetSchoolStaff(owner, a.Id));
        db.AssignEmployee(owner, employee.Id, b.Id, "انتداب", DateOnly.FromDateTime(DateTime.Today), null);
        var origin = db.GetSchoolOperationalSnapshot(owner, a.Id, "2026/2027");
        var destination = db.GetSchoolOperationalSnapshot(owner, b.Id, "2026/2027");
        Assert.Equal(1, origin.PayrollTeachers);
        Assert.Equal(0, origin.WorkplaceTeachers);
        Assert.Equal(0, destination.PayrollTeachers);
        Assert.Equal(1, destination.WorkplaceTeachers);
        Assert.Empty(db.GetSchoolStaff(owner, b.Id));
        Assert.Single(db.GetDistrictStaffDirectory(owner));
    }

    [Fact]
    public void DeliberateCensusMismatchIsDetectedWithoutRewritingApprovedHistory()
    {
        var (db, owner, a, _) = Setup();
        db.AddSchoolGrade(owner, a.Id, 1, "أ");
        db.SaveStudent(owner, new SchoolStudent("", a.Id, "", "طالب", 1, "أ",
            "", "", "", "", true, "2026/2027"));
        db.SaveDraft(owner, a.Id, new CensusData([new EnrollmentLine("1", 4, 0)], 1, 0, 2, "معتمد"));
        var official = db.GetLatestSubmission(owner, a.Id)!;
        db.CreateUser(owner, "principal3", "Strong-Password-123!", Roles.Principal, a.Id);
        var principal = db.Authenticate("principal3", "Strong-Password-123!")!;
        db.ChangeStatus(owner, official.Id, "submit");
        db.ChangeStatus(principal, official.Id, "school_approve");
        db.ChangeStatus(owner, official.Id, "district_approve");

        var results = db.GetCensusReconciliation(owner, "2026/2027");
        Assert.Contains(results, x => x.SchoolId == a.Id && x.MetricKey == "students.total" &&
            x.OperationalValue == 1 && x.ApprovedValue == 4 && x.Difference == -3);
        Assert.Contains(results, x => x.SchoolId == a.Id && x.MetricKey == "sections.total" &&
            x.OperationalValue == 1 && x.ApprovedValue == 2);
        Assert.Contains(results, x => x.SchoolId == a.Id && x.MetricKey == "teachers.workplace" &&
            x.OperationalValue == 0 && x.ApprovedValue == 1);
        Assert.Equal(4, db.GetLatestSubmission(owner, a.Id)!.Data.Students);
        Assert.Throws<InvalidOperationException>(() => db.SaveDraft(owner, a.Id,
            new CensusData([new EnrollmentLine("1", 1, 0)], 0, 0, 1, "")));
    }

    [Fact]
    public void NoApprovedSubmissionIsUnknownNotAnInventedZeroDiscrepancy()
    {
        var (db, owner, a, _) = Setup();
        db.AddSchoolGrade(owner, a.Id, 1, "أ");
        var comparison = db.GetCensusReconciliation(owner, "2026/2027");
        Assert.Contains(comparison, x => x.SchoolId == a.Id && x.MetricKey == "sections.total" &&
            x.ApprovedValue is null && x.Difference is null);
    }

    [Fact]
    public void ReconciliationReviewNeedsPermissionAndIsAuditOnly()
    {
        var (db, owner, a, b) = Setup();
        db.AddSchoolGrade(owner, a.Id, 1, "أ");
        db.CreateUser(owner, "schoolp3", "Strong-Password-123!", Roles.Entry, a.Id);
        var schoolUser = db.Authenticate("schoolp3", "Strong-Password-123!")!;
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.GetSchoolOperationalSnapshot(schoolUser, b.Id, "2026/2027"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.ReviewReconciliation(schoolUser, a.Id, "2026/2027",
                "sections.total", "تمت المراجعة"));
        Assert.Throws<ArgumentException>(() =>
            db.ReviewReconciliation(owner, a.Id, "2026/2027", "sections.total", ""));
        Assert.Throws<InvalidOperationException>(() =>
            db.ReviewReconciliation(owner, a.Id, "2026/2027", "unknown.metric", "مراجعة"));

        db.ReviewReconciliation(owner, a.Id, "2026/2027", "sections.total",
            "تحتاج المدرسة إلى استمارة معتمدة قبل المطابقة");
        Assert.Contains(db.GetRecentAudit(owner), x =>
            x.Action == "census.reconciliation.review" && x.EntityId == a.Id);
    }
}
