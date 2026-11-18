using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class Phase6ApprovalSnapshotRegressionTests
{
    [Fact]
    public void ANewerPendingRevisionMustNotHideTheLastOfficialApproval()
    {
        var path = Path.Combine(Path.GetTempPath(), "p6-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        var secret = "Secure-" + Guid.NewGuid().ToString("N") + "Aa1!";
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "p6founder", secret);
        var owner = db.Authenticate("p6founder", secret)!;
        db.AddSchool(owner, "P6-01", "مدرسة العليب", "العليب", "حكومية", "أساسي");
        var school = Assert.Single(db.GetSchools(owner));
        db.SaveDraft(owner, school.Id,
            new CensusData([new EnrollmentLine("1", 7, 0)], 1, 0, 1, ""));
        var census = db.GetLatestSubmission(owner, school.Id)!;
        var principalSecret = "Secure-" + Guid.NewGuid().ToString("N") + "Bb1!";
        db.CreateUser(owner, "p6principal", principalSecret, Roles.Principal, school.Id);
        var principal = db.Authenticate("p6principal", principalSecret)!;
        db.ChangeStatus(owner, census.Id, "submit");
        db.ChangeStatus(principal, census.Id, "school_approve");
        db.ChangeStatus(owner, census.Id, "district_approve");

        using (var connection = DatabaseSecurity.OpenForRegressionTest(path))
        using (var statement = connection.CreateCommand())
        {
            statement.CommandText = """
                INSERT INTO submissions
                (id,school_id,round_id,revision,status,created_by,
                 male_teachers,female_teachers,classrooms,notes,created_utc,updated_utc)
                SELECT $new,school_id,round_id,revision+1,'draft',created_by,
                       0,0,0,'',created_utc,updated_utc
                FROM submissions WHERE id=$original
                """;
            statement.Parameters.AddWithValue("$new", Guid.NewGuid().ToString("N"));
            statement.Parameters.AddWithValue("$original", census.Id);
            Assert.Equal(1, statement.ExecuteNonQuery());
        }

        var service = new ReportQueryService(db);
        var approved = service.Generate(owner,
            new ReportRequest(ReportKind.ApprovedCensus, new ReportFilter()));
        Assert.True(approved.IsApprovedSource);
        Assert.Equal(7, approved.Totals["students"]);
        Assert.Single(approved.Rows);
        var draft = service.Generate(owner,
            new ReportRequest(ReportKind.ApprovedCensus,
                new ReportFilter(ApprovalStatus: Statuses.Draft)));
        Assert.False(draft.IsApprovedSource);
        Assert.Single(draft.Rows);
    }

    [Fact]
    public void SchoolsSharingOneNameMustNotHaveTheirStaffCountsMerged()
    {
        var path = Path.Combine(Path.GetTempPath(), "p6-same-name-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        var secret = "Secure-" + Guid.NewGuid().ToString("N") + "Aa1!";
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "staffowner", secret);
        var owner = db.Authenticate("staffowner", secret)!;
        db.AddSchool(owner, "P6-NAME-A", "مدرسة الأمل", "العليب", "حكومية", "أساسي");
        db.AddSchool(owner, "P6-NAME-B", "مدرسة الأمل", "الريدة", "حكومية", "أساسي");
        var schools = db.GetSchools(owner);
        foreach (var school in schools)
            db.SaveSchoolStaff(owner, new SchoolStaff("", school.Id, "EMP-" + school.Code,
                "معلم المدرسة " + school.Code, "معلم", "معلم", "رياضيات",
                "بكالوريوس", "ذكور", true));

        var report = new ReportQueryService(db).Generate(owner,
            new ReportRequest(ReportKind.StaffDistribution, new ReportFilter()));
        Assert.Equal(2, report.Totals["staff"]);
        Assert.Equal(2, report.RecordCount);
        Assert.Contains("رمز المدرسة", report.Columns);
        Assert.Contains(report.Rows, r => r.Cells.Contains("P6-NAME-A"));
        Assert.Contains(report.Rows, r => r.Cells.Contains("P6-NAME-B"));
    }
}
