using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class StoreTests
{
    private static (SqliteStore Db, UserAccount Admin, string Path) Build()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "school-observatory-" + Guid.NewGuid() + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("مديرية اختبار", "2026/2027", "root", "Strong-Password-123!");
        var admin = db.Authenticate("root", "Strong-Password-123!")!;
        return (db, admin, path);
    }

    [Fact] public void InitialSetupCannotRepeat()
    {
        var (db, _, _) = Build();
        Assert.Throws<InvalidOperationException>(() =>
            db.InitializeAdministrator("آخر", "2026/2027", "root2", "Strong-Password-456!"));
    }

    [Fact] public void SchoolScopedUserSeesOnlyOwnSchool()
    {
        var (db, admin, _) = Build();
        db.AddSchool(admin, "100", "مدرسة أولى", "شمال", "حكومية", "أساسي");
        db.AddSchool(admin, "200", "مدرسة ثانية", "جنوب", "أهلية", "ثانوي");
        var schools = db.GetSchools(admin);
        db.CreateUser(admin, "teacher01", "Strong-Password-123!", Roles.Entry, schools[0].Id);
        var teacher = db.Authenticate("teacher01", "Strong-Password-123!")!;
        Assert.Single(db.GetSchools(teacher));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetLatestSubmission(teacher, schools[1].Id));
    }

    [Fact] public void ApprovedTotalsOnlyAndReturnCreatesRevision()
    {
        var (db, admin, _) = Build();
        db.AddSchool(admin, "101", "مدرسة", "المنطقة", "حكومية", "أساسي");
        var school = Assert.Single(db.GetSchools(admin));
        db.SaveDraft(admin, school.Id, new CensusData([new EnrollmentLine("1", 12, 11)], 3, 2, 4, ""));
        Assert.Equal(0, db.GetDashboard(admin).Students);
        var draft = db.GetLatestSubmission(admin, school.Id)!;
        db.ChangeStatus(admin, draft.Id, "submit");
        db.ChangeStatus(admin, draft.Id, "return", "يرجى إعادة التدقيق");
        db.SaveDraft(admin, school.Id, new CensusData([new EnrollmentLine("1", 13, 11)], 3, 2, 4, ""));
        var revised = db.GetLatestSubmission(admin, school.Id)!;
        Assert.Equal(2, revised.Revision);
        Assert.NotEqual(draft.Id, revised.Id);
        db.ChangeStatus(admin, revised.Id, "submit");
        db.CreateUser(admin, "principal01", "Strong-Password-123!", Roles.Principal, school.Id);
        var principal = db.Authenticate("principal01", "Strong-Password-123!")!;
        db.ChangeStatus(principal, revised.Id, "school_approve");
        Assert.Equal(0, db.GetDashboard(admin).Students);
        db.ChangeStatus(admin, revised.Id, "district_approve");
        Assert.Equal(24, db.GetDashboard(admin).Students);
        Assert.Throws<InvalidOperationException>(() => db.SaveDraft(admin, school.Id,
            new CensusData([new EnrollmentLine("1", 40, 40)], 3, 2, 4, "")));
    }

    [Fact] public void InactiveSessionCannotReadSchoolDirectoryOrSubmittedStatistics()
    {
        var (db, admin, _) = Build();
        db.AddSchool(admin, "100", "مدرسة اختبار", "المنطقة", "حكومية", "أساسي");
        var disabled = admin with { IsActive = false };
        Assert.Throws<UnauthorizedAccessException>(() => db.GetSchools(disabled));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetLatestSubmissions(disabled));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetDashboard(disabled));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetRecentAudit(disabled));
    }

    [Fact] public void SchoolUserCannotEnumerateOtherSchoolsSubmissions()
    {
        var (db, admin, _) = Build();
        db.AddSchool(admin, "100", "المدرسة الأولى", "المنطقة", "حكومية", "أساسي");
        db.AddSchool(admin, "200", "المدرسة الثانية", "المنطقة", "حكومية", "أساسي");
        var schools = db.GetSchools(admin);
        foreach (var school in schools)
            db.SaveDraft(admin, school.Id, new CensusData([new EnrollmentLine("1", 5, 5)], 1, 1, 2, ""));
        db.CreateUser(admin, "schoolusr", "Strong-Password-123!", Roles.Entry, schools[0].Id);
        var user = db.Authenticate("schoolusr", "Strong-Password-123!")!;
        var submissions = db.GetLatestSubmissions(user);
        Assert.Single(submissions);
        Assert.Equal(schools[0].Id, submissions[0].SchoolId);
    }

    [Fact] public void CannotSkipApprovalOrInventReturnReason()
    {
        var (db, admin, _) = Build();
        db.AddSchool(admin, "101", "مدرسة", "المنطقة", "حكومية", "أساسي");
        var school = Assert.Single(db.GetSchools(admin));
        db.SaveDraft(admin, school.Id, new CensusData([new EnrollmentLine("1", 7, 8)], 2, 1, 1, ""));
        var census = db.GetLatestSubmission(admin, school.Id)!;
        Assert.Throws<UnauthorizedAccessException>(() => db.ChangeStatus(admin, census.Id, "district_approve"));
        Assert.Throws<UnauthorizedAccessException>(() => db.ChangeStatus(admin, census.Id, "school_approve"));
        db.ChangeStatus(admin, census.Id, "submit");
        Assert.Throws<ArgumentException>(() => db.ChangeStatus(admin, census.Id, "return"));
    }
}
