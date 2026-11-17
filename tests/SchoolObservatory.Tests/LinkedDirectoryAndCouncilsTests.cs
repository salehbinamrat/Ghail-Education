using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class LinkedDirectoryAndCouncilsTests
{
    private static (SqliteStore Db, UserAccount Owner, School First, School Second) NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "school-link-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "owner", "Strong-Founder-Password-123!");
        var owner = db.Authenticate("owner", "Strong-Founder-Password-123!")!;
        db.AddSchool(owner, "E01", "مدرسة العليب", "العليب", "حكومية", "أساسي", "ذكور");
        db.AddSchool(owner, "E02", "مدرسة الريدة", "الريدة", "حكومية", "أساسي", "إناث");
        var schools = db.GetSchools(owner);
        return (db, owner, schools.Single(x => x.Code == "E01"),
            schools.Single(x => x.Code == "E02"));
    }

    [Fact]
    public void ArabicGradeNamesOmitBasicSuffixButLegacyExcelLabelsStillImport()
    {
        Assert.Equal("أولى", GradeNames.ForStage("أساسي", 1));
        Assert.Equal("تاسعة", GradeNames.ForStage("أساسي", 9));
        Assert.Equal("أولى ثانوي", GradeNames.ForStage("ثانوي", 10));
        Assert.Equal("روضة أولى", GradeNames.ForStage("روضة", 1));
        Assert.True(GradeNames.TryParse("أولى أساسي", out var grade));
        Assert.Equal(1, grade);
        Assert.True(GradeNames.TryParse("ثالثة", out grade));
        Assert.Equal(3, grade);
        Assert.True(GradeNames.TryParse("الثالث الأساسي", out grade));
        Assert.Equal(3, grade);
    }

    [Fact]
    public void ParentCouncilMembersAreLinkedToSchoolAndEditableWithoutDuplicateRecords()
    {
        var (db, owner, first, second) = NewStore();
        var created = db.SaveParentCouncilMember(owner, first.Id,
            new ParentCouncilMember("", first.Id, "مجلس الآباء", "أحمد بن سالم",
                "رئيس المجلس", "777000111", "متابعة التعليم", true));
        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.Equal("أحمد بن سالم", Assert.Single(db.GetParentCouncilMembers(owner, first.Id)).FullName);
        var edited = db.SaveParentCouncilMember(owner, first.Id,
            created with { FullName = "أحمد سالم", Phone = "777222333" });
        Assert.Equal(created.Id, edited.Id);
        Assert.Equal("أحمد سالم", Assert.Single(db.GetParentCouncilMembers(owner, first.Id)).FullName);
        Assert.Throws<UnauthorizedAccessException>(() => db.SaveParentCouncilMember(owner, second.Id,
            created with { SchoolId = second.Id }));
        Assert.Throws<UnauthorizedAccessException>(() => db.SetParentCouncilMemberActive(owner, second.Id, created.Id, false));
        db.SetParentCouncilMemberActive(owner, first.Id, created.Id, false);
        Assert.DoesNotContain(db.GetParentCouncilMembers(owner, first.Id), x => x.IsActive);
        Assert.Single(db.GetParentCouncilMembers(owner, first.Id)); // archived, not erased
        db.SetParentCouncilMemberActive(owner, first.Id, created.Id, true);
        Assert.True(Assert.Single(db.GetParentCouncilMembers(owner, first.Id)).IsActive);
    }

    [Fact]
    public void AutomaticStudentNumbersAndLiveDirectoryCountsUpdateAfterEachSave()
    {
        var (db, owner, school, _) = NewStore();
        var baseline = db.GetLiveDirectoryTotals(owner);
        Assert.Equal(0, baseline.ActiveStudents);
        db.AddSchoolGrade(owner, school.Id, 1, "أ");
        db.AddSchoolGrade(owner, school.Id, 1, "ب"); // one grade with two independent sections
        db.SaveStudent(owner, new SchoolStudent("", school.Id, "", "أحمد سالم",
            1, "أ", "", "سالم", "777000111", "", true, "2026/2027"));
        db.SaveStudent(owner, new SchoolStudent("", school.Id, "", "خالد سالم",
            1, "ب", "", "سالم", "777000222", "", true, "2026/2027"));
        var students = db.GetStudents(owner, school.Id);
        Assert.Equal(2, students.Count);
        Assert.Equal(2, students.Select(x => x.StudentCode).Distinct().Count());
        Assert.All(students, x => Assert.Matches("^[0-9]+$", x.StudentCode));
        db.SaveSchoolStaff(owner, new SchoolStaff("", school.Id, "T01", "معلم أول",
            "معلم", "معلم", "رياضيات", "بكالوريوس", "ذكور", true));
        db.SaveParentCouncilMember(owner, school.Id, new ParentCouncilMember("", school.Id,
            "مجلس الآباء", "رئيس المجلس", "رئيس", "777000333", "", true));
        var live = db.GetLiveDirectoryTotals(owner);
        Assert.Equal(2, live.ActiveStudents);
        Assert.Equal(1, live.ActiveTeachers);
        Assert.Equal(2, live.Sections);
        Assert.Equal(1, live.CouncilMembers);
        Assert.Equal(0, db.GetDashboard(owner).Students); // census is still approval-controlled
        var student = students[0];
        db.SaveStudent(owner, student with { IsActive = false }); // archive, preserve outcomes
        var staff = Assert.Single(db.GetSchoolStaff(owner, school.Id));
        db.SaveSchoolStaff(owner, staff with { IsActive = false });
        live = db.GetLiveDirectoryTotals(owner);
        Assert.Equal(1, live.ActiveStudents);
        Assert.Equal(0, live.ActiveTeachers);
        Assert.Single(db.GetStudents(owner, school.Id), s => !s.IsActive);
    }

    [Fact]
    public void SchoolScopedViewerCannotSeeForeignCountsOrEditCouncil()
    {
        var (db, owner, first, second) = NewStore();
        db.AddSchoolGrade(owner, second.Id, 1, "أ");
        db.SaveStudent(owner, new SchoolStudent("", second.Id, "", "الطالب",
            1, "أ", "", "", "", "", true, "2026/2027"));
        db.CreateUser(owner, "school_viewer", "Strong-Viewer-Password-123!", Roles.Entry,
            first.Id, (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStudents));
        var viewer = db.Authenticate("school_viewer", "Strong-Viewer-Password-123!")!;
        Assert.Equal(0, db.GetLiveDirectoryTotals(viewer).ActiveStudents);
        Assert.Throws<UnauthorizedAccessException>(() => db.GetParentCouncilMembers(viewer, second.Id));
        Assert.Throws<UnauthorizedAccessException>(() => db.SaveParentCouncilMember(viewer, first.Id,
            new ParentCouncilMember("", first.Id, "مجلس الآباء", "اسم", "عضو", "", "", true)));
    }

    [Fact]
    public void LoginRememberMeAndSequentialNumberingAreExplicitUiFeatures()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SchoolObservatory.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var basePath = Path.Combine(dir!.FullName, "src", "SchoolObservatory.Desktop");
        var loginXaml = File.ReadAllText(Path.Combine(basePath, "LoginWindow.xaml"));
        var loginCode = File.ReadAllText(Path.Combine(basePath, "LoginWindow.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(basePath, "MainWindow.xaml.cs"));
        var vault = File.ReadAllText(Path.Combine(basePath, "WindowsCredentialVault.cs"));
        Assert.Contains("حفظ اسم المستخدم وكلمة المرور", loginXaml, StringComparison.Ordinal);
        Assert.Contains("WindowsCredentialVault", loginCode, StringComparison.Ordinal);
        Assert.Contains("CredWrite", vault, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", vault, StringComparison.Ordinal);
        Assert.Contains("رقم تسلسلي", main, StringComparison.Ordinal);
    }
}
