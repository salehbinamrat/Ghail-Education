using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class DirectoryDetailProfilesTests
{
    private static (SqliteStore Store, UserAccount Owner, School First, School Second, string Path)
        Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "directory-detail-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "owner", "Strong-Founder-Password-123!");
        var owner = db.Authenticate("owner", "Strong-Founder-Password-123!")!;
        db.AddSchool(owner, "G01", "مدرسة العليب", "العليب", "حكومية", "أساسي", "ذكور");
        db.AddSchool(owner, "G02", "مدرسة الريدة", "الريدة", "حكومية", "ثانوي", "إناث");
        var schools = db.GetSchools(owner);
        return (db, owner, schools.Single(s => s.Code == "G01"),
            schools.Single(s => s.Code == "G02"), path);
    }

    [Fact]
    public void SchoolDetailsAreAdditiveAndPersistWithoutChangingSchoolRecord()
    {
        var (db, owner, school, _, path) = Create();
        var original = db.GetSchoolDirectoryDetails(owner, school.Id);
        Assert.Equal("", original.PrincipalName);
        db.SaveSchoolDirectoryDetails(owner, school.Id,
            new SchoolDirectoryDetails("مدير المدرسة", "777111222", "العليب - غيل بن يمين",
                1998, "مبنى مدرسي وملحقات"));
        var saved = db.GetSchoolDirectoryDetails(owner, school.Id);
        Assert.Equal("مدير المدرسة", saved.PrincipalName);
        Assert.Equal(1998, saved.FoundedYear);
        Assert.Equal(school.Id, db.GetSchools(owner).Single(s => s.Code == school.Code).Id);
        db.Initialize();
        var reopened = new SqliteStore(path);
        reopened.Initialize();
        Assert.Equal(saved, reopened.GetSchoolDirectoryDetails(owner, school.Id));
    }

    [Fact]
    public void SchoolScopedUserCannotReadOrWriteAnotherSchoolDetails()
    {
        var (db, owner, first, second, _) = Create();
        db.CreateUser(owner, "editor_school", "Strong-Editor-Password-123!", Roles.Entry, first.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ManageSchoolProfile));
        var user = db.Authenticate("editor_school", "Strong-Editor-Password-123!")!;
        db.SaveSchoolDirectoryDetails(user, first.Id,
            new SchoolDirectoryDetails("مدير الأولى", "", "", null, ""));
        Assert.Equal("مدير الأولى", db.GetSchoolDirectoryDetails(user, first.Id).PrincipalName);
        Assert.Throws<UnauthorizedAccessException>(() => db.GetSchoolDirectoryDetails(user, second.Id));
        Assert.Throws<UnauthorizedAccessException>(() => db.SaveSchoolDirectoryDetails(user, second.Id,
            new SchoolDirectoryDetails("تعديل غير مصرح", "", "", null, "")));
        Assert.Equal("", db.GetSchoolDirectoryDetails(owner, second.Id).PrincipalName);
    }

    [Fact]
    public void EmployeeContractAndSecondmentHaveSeparateAuditedPersonnelDetails()
    {
        var (db, owner, school, _, path) = Create();
        db.SaveSchoolStaff(owner, new SchoolStaff("", school.Id, "T01", "معلم العليب",
            "معلم", "معلم أول", "رياضيات", "بكالوريوس", "ذكور", true));
        db.SaveSchoolStaff(owner, new SchoolStaff("", school.Id, "A01", "الإداري",
            "إداري", "وكيل", "", "دبلوم", "ذكور", true));
        var staff = db.GetSchoolStaff(owner, school.Id);
        var teacher = staff.Single(s => s.EmployeeCode == "T01");
        var admin = staff.Single(s => s.EmployeeCode == "A01");

        db.SaveStaffPersonnel(owner, school.Id, teacher.Id,
            new StaffPersonnelDetails("موظف", 2016, null, 2012, "منتدب",
                "مكتب التربية والتعليم", 2024, "777123456", "باق في كشوفات المدرسة"));
        db.SaveStaffPersonnel(owner, school.Id, admin.Id,
            new StaffPersonnelDetails("متعاقد", null, 2023, 2019, "مباشر",
                "", null, "", "يعمل في المدرسة"));
        var first = db.GetStaffPersonnel(owner, school.Id, teacher.Id);
        var second = db.GetStaffPersonnel(owner, school.Id, admin.Id);
        Assert.Equal("منتدب", first.AssignmentStatus);
        Assert.Equal("مكتب التربية والتعليم", first.ActualWorkplace);
        Assert.Equal(2016, first.EmploymentYear);
        Assert.Equal(2024, first.AssignmentYear);
        Assert.Equal(2023, second.ContractYear);
        Assert.Equal("متعاقد", second.EmploymentType);
        Assert.Equal("معلم العليب", db.GetSchoolStaffMember(owner, school.Id, teacher.Id).FullName);
        Assert.Contains(db.GetRecentAudit(owner), a => a.Action == "staff.personnel_update");
        db.Initialize();
        var reopened = new SqliteStore(path);
        reopened.Initialize();
        Assert.Equal(first, reopened.GetStaffPersonnel(owner, school.Id, teacher.Id));
        Assert.Equal(second, reopened.GetStaffPersonnel(owner, school.Id, admin.Id));
    }

    [Fact]
    public void IncompatibleEmploymentDatesAndForeignSchoolAccessAreRejected()
    {
        var (db, owner, first, second, _) = Create();
        db.SaveSchoolStaff(owner, new SchoolStaff("", first.Id, "T02", "معلم",
            "معلم", "معلم", "", "دبلوم", "ذكور", true));
        var teacher = Assert.Single(db.GetSchoolStaff(owner, first.Id));
        Assert.Throws<ArgumentException>(() => db.SaveStaffPersonnel(owner, first.Id,
            teacher.Id, new StaffPersonnelDetails("موظف", null, null, null, "مباشر", "", null, "", "")));
        Assert.Throws<ArgumentException>(() => db.SaveStaffPersonnel(owner, first.Id,
            teacher.Id, new StaffPersonnelDetails("متعاقد", null, 2025, null, "منتدب", "", 2026, "", "")));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetSchoolStaffMember(owner, second.Id, teacher.Id));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetStaffPersonnel(owner, second.Id, teacher.Id));
        Assert.Throws<UnauthorizedAccessException>(() => db.SaveStaffPersonnel(owner, second.Id, teacher.Id,
            new StaffPersonnelDetails("موظف", 2015, null, null, "مباشر", "", null, "", "")));
        db.CreateUser(owner, "staff_view", "Strong-Viewer-Password-123!", Roles.Entry, first.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStaff));
        var viewer = db.Authenticate("staff_view", "Strong-Viewer-Password-123!")!;
        Assert.Equal("غير محدد", db.GetStaffPersonnel(viewer, first.Id, teacher.Id).EmploymentType);
        Assert.Throws<UnauthorizedAccessException>(() => db.SaveStaffPersonnel(viewer, first.Id, teacher.Id,
            new StaffPersonnelDetails("موظف", 2015, null, null, "مباشر", "", null, "", "")));
    }

    [Fact]
    public void ReimportOfSameEmployeeCannotEraseManuallyEnteredEmploymentAndSecondment()
    {
        var (db, owner, school, _, _) = Create();
        db.SaveSchoolStaff(owner, new SchoolStaff("", school.Id, "R01", "الاسم القديم",
            "معلم", "معلم", "رياضيات", "بكالوريوس", "ذكور", true));
        var person = Assert.Single(db.GetSchoolStaff(owner, school.Id));
        db.SaveStaffPersonnel(owner, school.Id, person.Id,
            new StaffPersonnelDetails("موظف", 2010, null, 2005, "منتدب",
                "إدارة الامتحانات", 2025, "", ""));
        db.ImportStaffRecords(owner,
        [
            new StaffImportRow(school.Code, "R01", "الاسم المصحح", "معلم", "معلم",
                "رياضيات", "بكالوريوس", "ذكور")
        ], "staff.xlsx");
        Assert.Equal("الاسم المصحح", db.GetSchoolStaffMember(owner, school.Id, person.Id).FullName);
        Assert.Equal("إدارة الامتحانات",
            db.GetStaffPersonnel(owner, school.Id, person.Id).ActualWorkplace);
        Assert.Equal(person.Id, Assert.Single(db.GetSchoolStaff(owner, school.Id)).Id);
    }

    [Fact]
    public void BothDirectoryTablesOpenAProfileOnRowDoubleClickNotHeaderClick()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SchoolObservatory.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var main = File.ReadAllText(Path.Combine(root!.FullName, "src",
            "SchoolObservatory.Desktop", "MainWindow.xaml.cs"));
        var profiles = File.ReadAllText(Path.Combine(root.FullName, "src",
            "SchoolObservatory.Desktop", "MainWindow.DirectoryProfiles.cs"));
        Assert.Contains("grid.MouseDoubleClick", main, StringComparison.Ordinal);
        Assert.Contains("staffGrid.MouseDoubleClick", main, StringComparison.Ordinal);
        Assert.Contains("DataGridRow", main, StringComparison.Ordinal);
        Assert.Contains("ShowSchoolProfile", profiles, StringComparison.Ordinal);
        Assert.Contains("ShowStaffProfile", profiles, StringComparison.Ordinal);
        Assert.Contains("ملف المدرسة", main, StringComparison.Ordinal);
        Assert.Contains("ملف الموظف", main, StringComparison.Ordinal);
    }
    [Fact]
    public void LocalDirectorySearchMatchesArabicNameVariantsAndEmployeeCode()
    {
        var schools = new[]
        {
            new School("1", "S01", "مدرسة الآمل", "غيل بن يمين", "حكومية", "أساسي", true),
            new School("2", "S02", "مدرسة الريدة", "الريدة", "حكومية", "أساسي", true)
        };
        Assert.Equal("S01", Assert.Single(DirectorySearch.FindSchools(schools, "الامل")).Code);
        var staff = new[]
        {
            new SchoolStaff("1", "1", "T001", "أحمد", "معلم", "معلم",
                "رياضيات", "بكالوريوس", "ذكور", true),
            new SchoolStaff("2", "1", "A002", "علي", "إداري", "وكيل",
                "", "دبلوم", "ذكور", true)
        };
        Assert.Equal("T001", Assert.Single(DirectorySearch.FindStaff(staff, "احمد")).EmployeeCode);
        Assert.Equal("A002", Assert.Single(DirectorySearch.FindStaff(staff, "a002")).EmployeeCode);
        Assert.Equal(2, DirectorySearch.FindStaff(staff, "").Count);
    }

}
