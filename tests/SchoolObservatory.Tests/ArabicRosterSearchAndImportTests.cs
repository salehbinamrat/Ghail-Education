using SchoolObservatory.Core;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class ArabicRosterSearchAndImportTests
{
    private static SchoolStudent Student(string code, string name, string guardian, string phone) =>
        new(code, "school-1", code, name, 3, "أ", "ذكور", guardian,
            phone, "", true, "2026/2027");

    [Fact]
    public void SearchMatchesArabicSpellingAndDiacriticsWithoutChangingStudentRecords()
    {
        var students = new[]
        {
            Student("001", "أَحْمَد سالم", "إبراهيم علي", "777123456"),
            Student("002", "خالد عمر", "محمد حسين", "733000111")
        };

        Assert.Same(students, SchoolStudentSearch.Find(students, "   "));
        Assert.Equal("001", Assert.Single(SchoolStudentSearch.Find(students, "احمد")).StudentCode);
        Assert.Equal("001", Assert.Single(SchoolStudentSearch.Find(students, "ابراهيم")).StudentCode);
        Assert.Equal("001", Assert.Single(SchoolStudentSearch.Find(students, "آحمد")).StudentCode);
        Assert.Equal("001", Assert.Single(SchoolStudentSearch.Find(students, "٠٠١")).StudentCode);
        Assert.Equal("001", Assert.Single(SchoolStudentSearch.Find(students, "۷۷۷۱۲۳۴۵۶")).StudentCode);
        Assert.Equal("002", Assert.Single(SchoolStudentSearch.Find(students, "733000111")).StudentCode);
        Assert.Empty(SchoolStudentSearch.Find(students, "غير موجود"));
        Assert.Equal("أَحْمَد سالم", students[0].FullName);
    }

    [Fact]
    public void BulkImportCachesClassIdentifiersForStudentsOfTheSameSection()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SchoolObservatory.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src",
            "SchoolObservatory.Data", "SqliteStore.Students.cs"));
        Assert.Contains("gradeIds.TryGetValue", source, StringComparison.Ordinal);
        Assert.Contains("gradeIds.Add", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BulkImportWithSharedAndDistinctSectionsKeepsStudentCountsCorrect()
    {
        var path = Path.Combine(Path.GetTempPath(), "student-grade-cache-" +
            Guid.NewGuid().ToString("N") + ".db");
        var store = new SchoolObservatory.Data.SqliteStore(path);
        store.Initialize();
        store.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder",
            "Secure-Founder-Password-123!");
        var admin = store.Authenticate("founder", "Secure-Founder-Password-123!")!;
        store.AddSchool(admin, "A01", "مدرسة اختبار", "العليب", "حكومية", "أساسي", "ذكور");
        var school = Assert.Single(store.GetSchools(admin));
        var third = store.AddSchoolGrade(admin, school.Id, 3, "أ");
        var fourth = store.AddSchoolGrade(admin, school.Id, 4, "ب");

        var rows = Enumerable.Range(1, 120)
            .Select(i => new StudentImportRow("A01", i.ToString("D4"),
                $"طالب {i}", i % 2 == 0 ? 3 : 4, i % 2 == 0 ? "أ" : "ب",
                "", "ولي الأمر", "777000111"))
            .ToArray();
        Assert.Equal(120, store.ImportStudents(admin, school.Id, rows, "students.xlsx"));
        Assert.Equal(60, store.GetStudents(admin, school.Id, third.Id).Count);
        Assert.Equal(60, store.GetStudents(admin, school.Id, fourth.Id).Count);
        Assert.Equal(120, store.ImportStudents(admin, school.Id, rows, "students.xlsx"));
        Assert.Equal(120, store.GetStudents(admin, school.Id).Count);
    }
}
