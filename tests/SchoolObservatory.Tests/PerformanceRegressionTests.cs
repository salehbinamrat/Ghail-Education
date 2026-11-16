using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class PerformanceRegressionTests
{
    private static (SqliteStore Store, UserAccount Admin, string Path) Prepare()
    {
        var path = Path.Combine(Path.GetTempPath(), "school-perf-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("مديرية اختبار", "2026/2027", "admin", "Safe-Test-Password-123!");
        return (db, db.Authenticate("admin", "Safe-Test-Password-123!")!, path);
    }

    [Fact]
    public void DashboardCountsLatestApprovedRevisionOnlyAndHonorsSchoolScope()
    {
        var (db, admin, _) = Prepare();
        db.ImportSchools(admin, [
            new SchoolImport("A", "مدرسة ألف", "شرق", "حكومية", "أساسي"),
            new SchoolImport("B", "مدرسة باء", "غرب", "حكومية", "أساسي"),
            new SchoolImport("C", "مدرسة جيم", "وسط", "أهلية", "ثانوي")
        ]);
        var schools = db.GetSchools(admin);
        var a = schools.Single(x => x.Code == "A");
        var b = schools.Single(x => x.Code == "B");
        var c = schools.Single(x => x.Code == "C");
        db.SaveDraft(admin, a.Id, new CensusData([new EnrollmentLine("1", 10, 10)], 1, 1, 2, ""));
        var original = db.GetLatestSubmission(admin, a.Id)!;
        db.ChangeStatus(admin, original.Id, "submit");
        db.ChangeStatus(admin, original.Id, "return", "تصحيح الأعداد");
        db.SaveDraft(admin, a.Id, new CensusData([
            new EnrollmentLine("1", 11, 12), new EnrollmentLine("2", 3, 4)
        ], 2, 1, 5, ""));
        var revised = db.GetLatestSubmission(admin, a.Id)!;
        Assert.Equal(2, revised.Revision);
        db.ChangeStatus(admin, revised.Id, "submit");
        db.CreateUser(admin, "principal_a", "Safe-Test-Password-123!", Roles.Principal, a.Id);
        var principal = db.Authenticate("principal_a", "Safe-Test-Password-123!")!;
        db.ChangeStatus(principal, revised.Id, "school_approve");
        db.ChangeStatus(admin, revised.Id, "district_approve");
        db.SaveDraft(admin, b.Id, new CensusData([new EnrollmentLine("1", 100, 100)], 20, 20, 20, ""));
        db.SaveDraft(admin, c.Id, new CensusData([new EnrollmentLine("10", 5, 6)], 1, 1, 1, ""));
        db.ChangeStatus(admin, db.GetLatestSubmission(admin, c.Id)!.Id, "submit");

        var district = db.GetDashboard(admin);
        Assert.Equal(3, district.Schools);
        Assert.Equal(1, district.Submitted);
        Assert.Equal(1, district.Approved);
        Assert.Equal(30, district.Students);
        Assert.Equal(3, district.Teachers);
        Assert.Equal(5, district.Classrooms);
        var own = db.GetDashboard(principal);
        Assert.Equal(1, own.Schools);
        Assert.Equal(0, own.Submitted);
        Assert.Equal(1, own.Approved);
        Assert.Equal(30, own.Students);
        Assert.Equal(3, db.GetLatestSubmissions(admin).Count);
        Assert.Equal(2, db.GetLatestSubmissions(principal).Single().Data.Rows.Count);
    }

    [Fact]
    public void BulkImportIsAtomicWhenOneCodeAlreadyExists()
    {
        var (db, admin, _) = Prepare();
        db.AddSchool(admin, "EXISTING", "مدرسة", "الشرق", "حكومية", "أساسي");
        var incoming = Enumerable.Range(1, 120).Select(i =>
            new SchoolImport("TEST" + i.ToString("D3"), "مدرسة " + i, "الغرب", "حكومية", "أساسي")).ToList();
        incoming.Add(new SchoolImport("EXISTING", "تكرار", "الغرب", "حكومية", "أساسي"));
        Assert.Throws<SqliteException>(() => db.ImportSchools(admin, incoming));
        Assert.Single(db.GetSchools(admin));
    }

    [Fact]
    public void LatestCensusIndexExistsAfterRepeatedInitialization()
    {
        var (db, _, path) = Prepare();
        db.Initialize();
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_submissions_round_school_revision'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }
}
