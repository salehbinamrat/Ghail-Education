using System.Xml.Linq;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class ArabicKindergartenAndEditingTests
{
    private static (SqliteStore Store, UserAccount Owner, string Path) Open()
    {
        var path = Path.Combine(Path.GetTempPath(), "school-kindergarten-" +
            Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteStore(path);
        store.Initialize();
        store.InitializeAdministrator("غيل بن يمين", "2026/2027",
            "founder", "Secure-Founder-Password-123!");
        return (store, store.Authenticate("founder", "Secure-Founder-Password-123!")!, path);
    }

    [Theory]
    [InlineData("أساسي", 1, "أولى أساسي")]
    [InlineData("أساسي", 9, "تاسعة أساسي")]
    [InlineData("ثانوي", 10, "أولى ثانوي")]
    [InlineData("ثانوي", 11, "ثانية ثانوي")]
    [InlineData("ثانوي", 12, "ثالثة ثانوي")]
    [InlineData("مجمع تربوي", 12, "ثالثة ثانوي")]
    [InlineData("روضة", 1, "روضة أولى")]
    [InlineData("روضة", 2, "روضة ثانية")]
    [InlineData("روضة", 3, "تمهيدي")]
    public void GradeNamesAreArabicWithoutChangingStoredNumericGrade(string stage, int grade, string expected) =>
        Assert.Equal(expected, GradeNames.ForStage(stage, grade));

    [Theory]
    [InlineData("أولى ثانوي", 10)]
    [InlineData("ثالثة ثانوي", 12)]
    [InlineData("روضة ثانية", 2)]
    [InlineData("تمهيدي", 3)]
    [InlineData("الثالث الأساسي", 3)]
    [InlineData("٣", 3)]
    public void ImportAcceptsArabicGradeNames(string label, int expected)
    {
        Assert.True(GradeNames.TryParse(label, out var grade));
        Assert.Equal(expected, grade);
    }

    [Fact]
    public void KindergartenSurvivesReopenAndRejectsIncompatibleGrades()
    {
        var (store, owner, path) = Open();
        store.AddSchool(owner, "KG1", "روضة العليب", "العليب", "حكومية", "روضة", "مختلطة");
        var school = Assert.Single(store.GetSchools(owner));
        Assert.Equal("روضة", school.Stage);
        var grade = store.AddSchoolGrade(owner, school.Id, 1, "أ");
        Assert.Equal("روضة أولى — الشعبة أ", grade.DisplayName);
        Assert.Throws<ArgumentException>(() => store.AddSchoolGrade(owner, school.Id, 4, "أ"));
        store.Initialize();
        var reloaded = new SqliteStore(path);
        reloaded.Initialize();
        Assert.Equal("روضة", Assert.Single(reloaded.GetSchools(owner)).Stage);
        Assert.Equal("روضة أولى — الشعبة أ", Assert.Single(reloaded.GetSchoolGrades(owner, school.Id)).DisplayName);
        Assert.Throws<InvalidOperationException>(() =>
            reloaded.UpdateSchool(owner, school with { Stage = "أساسي" }));
    }

    [Fact]
    public void CorrectingClassAndSectionPreservesStudentIdentityAndResults()
    {
        var (store, owner, _) = Open();
        store.AddSchool(owner, "S1", "مدرسة الاختبار", "العليب", "حكومية", "أساسي", "ذكور");
        var school = Assert.Single(store.GetSchools(owner));
        var grade = store.AddSchoolGrade(owner, school.Id, 2, "أ");
        store.SaveStudent(owner, new SchoolStudent("", school.Id, "0001", "أحمد سالم", 2, "أ",
            "", "ولي الأمر", "777000000", "", true, "2026/2027"));
        var student = Assert.Single(store.GetStudents(owner, school.Id));
        store.SaveStudentResult(owner, school.Id, student.Id, "الفصل الأول", "العربية", 90, 100, "");
        store.UpdateSchoolGrade(owner, school.Id, grade.Id, 2, "ب");
        var updated = Assert.Single(store.GetStudents(owner, school.Id));
        Assert.Equal(student.Id, updated.Id);
        Assert.Equal("ب", updated.Section);
        Assert.Single(store.GetStudentResults(owner, school.Id, student.Id));
        Assert.Equal("ثانية أساسي — الشعبة ب",
            Assert.Single(store.GetSchoolGrades(owner, school.Id)).DisplayName);
    }

    [Fact]
    public void ClassCorrectionCannotCrossSchoolsOrOverwriteAnotherClass()
    {
        var (store, owner, _) = Open();
        store.AddSchool(owner, "S1", "مدرسة الأولى", "العليب", "حكومية", "أساسي", "ذكور");
        store.AddSchool(owner, "S2", "مدرسة الثانية", "العليب", "حكومية", "أساسي", "ذكور");
        var schools = store.GetSchools(owner);
        var one = schools.Single(x => x.Code == "S1");
        var two = schools.Single(x => x.Code == "S2");
        var a = store.AddSchoolGrade(owner, one.Id, 1, "أ");
        store.AddSchoolGrade(owner, one.Id, 1, "ب");
        Assert.Throws<UnauthorizedAccessException>(() => store.UpdateSchoolGrade(owner,
            two.Id, a.Id, 1, "ج"));
        Assert.Throws<InvalidOperationException>(() => store.UpdateSchoolGrade(owner,
            one.Id, a.Id, 1, "ب"));
        Assert.Equal("أ", store.GetSchoolGrades(owner, one.Id).Single(g => g.Id == a.Id).Section);
    }

    [Fact]
    public void ArabicArialAndVisibleNavigationSelectionHaveContracts()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SchoolObservatory.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var app = File.ReadAllText(Path.Combine(dir!.FullName, "src", "SchoolObservatory.Desktop", "App.xaml"));
        var main = File.ReadAllText(Path.Combine(dir.FullName, "src", "SchoolObservatory.Desktop", "MainWindow.xaml.cs"));
        Assert.Contains("Arial", app, StringComparison.Ordinal);
        Assert.Contains("UpdateActiveNavigation", main, StringComparison.Ordinal);
        Assert.Contains("روضة", main, StringComparison.Ordinal);
        Assert.Contains("تعديل الصف والشعبة", File.ReadAllText(Path.Combine(dir.FullName,
            "src", "SchoolObservatory.Desktop", "MainWindow.Students.cs")), StringComparison.Ordinal);
    }
}
