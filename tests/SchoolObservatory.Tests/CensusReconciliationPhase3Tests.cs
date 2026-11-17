using Microsoft.Data.Sqlite;
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
    public void AnApprovedCensusCannotBeOverwrittenThroughRawDatabaseCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), "phase3-lock-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Strong-Founder-Password-123!");
        var owner = db.Authenticate("founder", "Strong-Founder-Password-123!")!;
        db.AddSchool(owner, "LOCK-1", "مدرسة الحصون", "الحصون", "حكومية", "أساسي", "ذكور");
        var school = Assert.Single(db.GetSchools(owner));
        db.SaveDraft(owner, school.Id, new CensusData([new EnrollmentLine("1", 2, 0)], 1, 0, 1, ""));
        var submitted = db.GetLatestSubmission(owner, school.Id)!;
        db.CreateUser(owner, "principal4", "Strong-Password-123!", Roles.Principal, school.Id);
        var principal = db.Authenticate("principal4", "Strong-Password-123!")!;
        db.ChangeStatus(owner, submitted.Id, "submit");
        db.ChangeStatus(principal, submitted.Id, "school_approve");
        db.ChangeStatus(owner, submitted.Id, "district_approve");
        using var con = DatabaseSecurity.OpenForRegressionTest(path);
        using var overwrite = con.CreateCommand();
        overwrite.CommandText = "UPDATE submissions SET male_teachers=999 WHERE id=$id";
        overwrite.Parameters.AddWithValue("$id", submitted.Id);
        Assert.Throws<SqliteException>(() => overwrite.ExecuteNonQuery());
        using var child = con.CreateCommand();
        child.CommandText = "UPDATE enrollment_rows SET boys=999 WHERE submission_id=$id";
        child.Parameters.AddWithValue("$id", submitted.Id);
        Assert.Throws<SqliteException>(() => child.ExecuteNonQuery());
        Assert.Equal(2, db.GetLatestSubmission(owner, school.Id)!.Data.Students);
        Assert.Equal(1, db.GetLatestSubmission(owner, school.Id)!.Data.Teachers);
    }

    [Fact]
    public void UpgradingSchemaThreeKeepsApprovedAndOriginalRecordIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), "phase3-v3-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Strong-Founder-Password-123!");
        var owner = db.Authenticate("founder", "Strong-Founder-Password-123!")!;
        db.AddSchool(owner, "MIG-1", "مدرسة العام", "العليب", "حكومية", "أساسي", "ذكور");
        var identity = Assert.Single(db.GetSchools(owner));
        using (var con = DatabaseSecurity.OpenForRegressionTest(path))
        {
            using var old = con.CreateCommand();
            old.CommandText = "PRAGMA user_version=3; DROP TRIGGER IF EXISTS p3_approved_submission_update;";
            old.ExecuteNonQuery();
        }
        db.Initialize();
        Assert.Equal(identity.Id, Assert.Single(db.GetSchools(owner)).Id);
        using var verify = DatabaseSecurity.OpenForRegressionTest(path);
        using var check = verify.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        Assert.Equal(4L, Convert.ToInt64(check.ExecuteScalar()));
        Assert.NotEmpty(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(path)!, "recovery"), "before-schema-*.db"));
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
    public void DeliberatelyCorruptedEmployeeAssignmentIsFlaggedNotAutoCorrected()
    {
        var path = Path.Combine(Path.GetTempPath(), "phase3-corruption-" +
            Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Strong-Founder-Password-123!");
        var owner = db.Authenticate("founder", "Strong-Founder-Password-123!")!;
        db.AddSchool(owner, "C01", "مدرسة الملاك", "القرية", "حكومية", "أساسي", "ذكور");
        db.AddSchool(owner, "C02", "مدرسة العمل", "القرية", "حكومية", "أساسي", "ذكور");
        var schools = db.GetSchools(owner);
        var payroll = schools.Single(x => x.Code == "C01");
        var workplace = schools.Single(x => x.Code == "C02");
        db.SaveSchoolStaff(owner, new SchoolStaff("", payroll.Id, "EMP-17", "موظف",
            "معلم", "معلم", "رياضيات", "بكالوريوس", "ذكور", true));
        var person = Assert.Single(db.GetSchoolStaff(owner, payroll.Id));
        using (var con = DatabaseSecurity.OpenForRegressionTest(path))
        {
            using var tamper = con.CreateCommand();
            tamper.CommandText = """
                UPDATE employee_assignments SET payroll_school_id=$wrong
                WHERE employee_id=$id
                """;
            tamper.Parameters.AddWithValue("$wrong", workplace.Id);
            tamper.Parameters.AddWithValue("$id", person.Id);
            Assert.Equal(1, tamper.ExecuteNonQuery());
        }
        var problems = db.GetCensusReconciliation(owner, "2026/2027");
        Assert.Contains(problems, x => x.SchoolId == payroll.Id &&
            x.MetricKey == "integrity.staff_assignment" &&
            x.OperationalValue > 0 && x.IsDiscrepant);
        Assert.Equal(payroll.Id, db.GetEmployeeCareer(owner, person.Id).OriginalPayrollSchoolId);
        Assert.Single(db.GetSchoolStaff(owner, payroll.Id));
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
