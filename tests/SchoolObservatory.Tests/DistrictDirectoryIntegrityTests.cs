using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

/// <summary>Prevents disagreement between the school, district staff, assignment and dashboard views.</summary>
public sealed class DistrictDirectoryIntegrityTests
{
    private static (SqliteStore Store, UserAccount Owner, School A, School B, string DbPath) New()
    {
        var path = Path.Combine(Path.GetTempPath(), "school-integrity-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteStore(path);
        store.Initialize();
        store.InitializeAdministrator("غيل بن يمين", "2026/2027",
            "owner", "Strong-Founder-Password-123!");
        var owner = store.Authenticate("owner", "Strong-Founder-Password-123!")!;
        store.AddSchool(owner, "A01", "مدرسة العليب", "العليب", "حكومية", "أساسي", "ذكور");
        store.AddSchool(owner, "B01", "مدرسة الريدة", "الريدة", "حكومية", "أساسي", "إناث");
        var schools = store.GetSchools(owner);
        return (store, owner, schools.Single(s => s.Code == "A01"),
            schools.Single(s => s.Code == "B01"), path);
    }

    private static SchoolStaff Member(School school, string code, string name = "المدرس") =>
        new("", school.Id, code, name, "معلم", "معلم أول", "رياضيات", "بكالوريوس", "ذكور", true);

    [Fact]
    public void SamePayrollNumberCannotBeCreatedInTwoDifferentSchools()
    {
        var (db, owner, a, b, _) = New();
        db.SaveSchoolStaff(owner, Member(a, "T-107"));
        Assert.Throws<ArgumentException>(() =>
            db.SaveSchoolStaff(owner, Member(b, "t-107", "معلم آخر")));
        Assert.Single(db.GetSchoolStaff(owner, a.Id));
        Assert.Empty(db.GetSchoolStaff(owner, b.Id));
        var teacher = Assert.Single(db.GetSchoolStaff(owner, a.Id));
        db.SaveSchoolStaff(owner, teacher with { FullName = "اسم مصحح" });
        Assert.Equal(teacher.Id, Assert.Single(db.GetSchoolStaff(owner, a.Id)).Id);
    }

    [Fact]
    public void BulkImportsRejectCrossSchoolDuplicateBeforeAnyRowIsCommitted()
    {
        var (db, owner, a, b, _) = New();
        db.SaveSchoolStaff(owner, Member(a, "T-200"));
        var list = new[]
        {
            new StaffImportRow(b.Code, "T-201", "طالب وظيفة", "معلم", "معلم", "", "", "ذكور"),
            new StaffImportRow(b.Code, "T-200", "تكرار آخر", "معلم", "معلم", "", "", "ذكور")
        };
        Assert.Throws<ArgumentException>(() => db.ImportStaffRecords(owner, list, "employees.xlsx"));
        Assert.Empty(db.GetSchoolStaff(owner, b.Id));
        Assert.Single(db.GetSchoolStaff(owner, a.Id));
        Assert.Throws<ArgumentException>(() =>
            db.ImportStaffRecords(owner,
            [
                new StaffImportRow(a.Code, "T-300", "واحد", "معلم", "معلم", "", "", "ذكور"),
                new StaffImportRow(b.Code, "t-300", "اثنان", "إداري", "وكيل", "", "", "ذكور")
            ], "mixed.xlsx"));
        Assert.Empty(db.GetSchoolStaff(owner, b.Id));
    }

    [Fact]
    public void DistrictDirectoryUsesSameStaffIdAsSchoolAndResolvesActualWorkplace()
    {
        var (db, owner, a, b, _) = New();
        db.SaveSchoolStaff(owner, Member(a, "T-401", "معلم منتدب"));
        db.SaveSchoolStaff(owner, Member(b, "T-402", "معلم مباشر"));
        var first = Assert.Single(db.GetSchoolStaff(owner, a.Id));
        var second = Assert.Single(db.GetSchoolStaff(owner, b.Id));
        db.SaveStaffPersonnel(owner, a.Id, first.Id,
            new StaffPersonnelDetails("موظف", 2015, null, 2012, "منتدب",
                "مكتب التربية والتعليم", 2024, "", ""));
        db.SaveStaffPersonnel(owner, b.Id, second.Id,
            new StaffPersonnelDetails("موظف", 2018, null, 2016, "مباشر",
                "", null, "", ""));
        var directory = db.GetDistrictStaffDirectory(owner);
        Assert.Equal(2, directory.Count);
        var sent = directory.Single(s => s.EmployeeCode == "T-401");
        Assert.Equal(first.Id, sent.StaffId);
        Assert.Equal(a.Id, sent.SchoolId);
        Assert.Equal(a.Name, sent.SchoolName);
        Assert.Equal("مكتب التربية والتعليم", sent.EffectiveWorkplace);
        Assert.Equal("منتدب", sent.AssignmentStatus);
        Assert.Equal(b.Name, directory.Single(s => s.EmployeeCode == "T-402").EffectiveWorkplace);
        Assert.Equal(second.Id, directory.Single(s => s.EmployeeCode == "T-402").StaffId);
    }

    [Fact]
    public void LiveCountersSeparateDirectSecondedAndUnknownInsteadOfAssumingPresent()
    {
        var (db, owner, a, _, _) = New();
        db.SaveSchoolStaff(owner, Member(a, "T-501", "مباشر"));
        db.SaveSchoolStaff(owner, Member(a, "T-502", "منتدب"));
        db.SaveSchoolStaff(owner, Member(a, "T-503", "غير محدد"));
        var people = db.GetSchoolStaff(owner, a.Id);
        var direct = people.Single(x => x.EmployeeCode == "T-501");
        var seconded = people.Single(x => x.EmployeeCode == "T-502");
        db.SaveStaffPersonnel(owner, a.Id, direct.Id,
            new StaffPersonnelDetails("موظف", 2012, null, null, "مباشر", "", null, "", ""));
        db.SaveStaffPersonnel(owner, a.Id, seconded.Id,
            new StaffPersonnelDetails("موظف", 2014, null, null, "منتدب",
                "مكتب المديرية", 2025, "", ""));
        var stats = db.GetLiveDirectoryTotals(owner);
        Assert.Equal(3, stats.ActiveTeachers); // school payroll roster
        Assert.Equal(1, stats.DirectStaff);
        Assert.Equal(1, stats.SecondedStaff);
        Assert.Equal(1, stats.UnclassifiedStaff);
        db.SaveSchoolStaff(owner, seconded with { IsActive = false });
        stats = db.GetLiveDirectoryTotals(owner);
        Assert.Equal(2, stats.ActiveTeachers);
        Assert.Equal(0, stats.SecondedStaff);
        Assert.Equal(1, stats.DirectStaff);
        Assert.Equal(1, stats.UnclassifiedStaff);
    }

    [Fact]
    public void ExistingLegacyConflictsRemainReadableAndAppearInIntegrityReview()
    {
        var (db, owner, a, b, path) = New();
        db.SaveSchoolStaff(owner, Member(a, "LEG-1"));
        // An old database may already have duplicate payroll codes across schools;
        // additive migrations must never delete either historical row.
        using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO school_staff(id,school_id,employee_code,full_name,category,
                    job_title,specialization,qualification,gender,is_active,created_utc,updated_utc)
                VALUES($id,$school,'leg-1','سجل قديم','إداري','','','','ذكور',1,$now,$now)
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$school", b.Id);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        db.Initialize();
        Assert.Single(db.GetSchoolStaff(owner, a.Id));
        Assert.Single(db.GetSchoolStaff(owner, b.Id));
        var conflict = Assert.Single(db.GetDistrictStaffCodeConflicts(owner));
        Assert.Equal("LEG-1", conflict.EmployeeCode, ignoreCase: true);
        Assert.Equal(2, conflict.Schools);
        Assert.Equal(1, db.GetLiveDirectoryTotals(owner).DuplicateEmployeeCodes);
    }

    [Fact]
    public void SchoolBoundUsersGetOnlyOwnSchoolDirectoryWithoutConflictLeaks()
    {
        var (db, owner, a, b, _) = New();
        db.SaveSchoolStaff(owner, Member(a, "A-1"));
        db.SaveSchoolStaff(owner, Member(b, "B-1"));
        db.CreateUser(owner, "school_user", "Strong-Viewer-Password-123!", Roles.Entry, a.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStaff));
        var scoped = db.Authenticate("school_user", "Strong-Viewer-Password-123!")!;
        var row = Assert.Single(db.GetDistrictStaffDirectory(scoped));
        Assert.Equal(a.Id, row.SchoolId);
        Assert.Throws<UnauthorizedAccessException>(() => db.GetDistrictStaffCodeConflicts(scoped));
        Assert.Equal(0, db.GetLiveDirectoryTotals(scoped).DuplicateEmployeeCodes);
    }

    [Fact]
    public void WpfUsesBoldArialInEveryBasicControlAndDirectoryOffersUnifiedStaffView()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SchoolObservatory.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var basePath = Path.Combine(root!.FullName, "src", "SchoolObservatory.Desktop");
        var resources = File.ReadAllText(Path.Combine(basePath, "App.xaml"));
        var main = File.ReadAllText(Path.Combine(basePath, "MainWindow.xaml.cs"));
        foreach (var control in new[] { "Window", "TextBlock", "TextBox", "PasswordBox", "Button",
            "ComboBox", "ComboBoxItem", "CheckBox", "DataGrid", "DataGridCell",
            "DataGridColumnHeader", "DataGridRow", "TabItem" })
        {
            var tag = "<Style TargetType=\"" + control + "\"";
            Assert.Contains(tag, resources, StringComparison.Ordinal);
            var start = resources.IndexOf(tag, StringComparison.Ordinal);
            var end = resources.IndexOf("</Style>", start, StringComparison.Ordinal);
            if (end < 0) end = resources.IndexOf("/>", start, StringComparison.Ordinal);
            Assert.Contains("Property=\"FontWeight\" Value=\"Bold\"",
                resources[start..(end + 2)], StringComparison.Ordinal);
        }
        Assert.Contains("GetDistrictStaffDirectory", main, StringComparison.Ordinal);
        Assert.Contains("ملف الموظف", main, StringComparison.Ordinal);
        Assert.DoesNotContain("FontWeights.Normal", main, StringComparison.Ordinal);
    }
}
