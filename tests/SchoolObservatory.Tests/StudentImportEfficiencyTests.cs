using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class StudentImportEfficiencyTests
{
    [Fact]
    public void ReimportKeepsStudentIdentityAndResultsForLargeClass()
    {
        var path = Path.Combine(Path.GetTempPath(), "students-upsert-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteStore(path);
        store.Initialize();
        store.InitializeAdministrator("مديرية اختبار", "2026/2027", "founder", "Secure-Founder-Password-123!");
        var owner = store.Authenticate("founder", "Secure-Founder-Password-123!")!;
        store.AddSchool(owner, "IMP", "مدرسة الاختبار", "المديرية", "حكومية", "أساسي", "ذكور");
        var school = Assert.Single(store.GetSchools(owner));
        var grade = store.AddSchoolGrade(owner, school.Id, 3, "أ");
        var rows = Enumerable.Range(1, 250)
            .Select(i => new StudentImportRow("IMP", i.ToString("D5"), $"طالب {i}", 3,
                "أ", "", "ولي الأمر", "777000111"))
            .ToArray();

        Assert.Equal(250, store.ImportStudents(owner, school.Id, rows, "sample.xlsx"));
        var existing = store.GetStudents(owner, school.Id, grade.Id);
        Assert.Equal(250, existing.Count);
        var identities = existing.ToDictionary(x => x.StudentCode, x => x.Id);
        var first = existing.Single(x => x.StudentCode == "00001");
        store.SaveStudentResult(owner, school.Id, first.Id,
            "الفصل الأول", "الرياضيات", 91, 100, "موثقة");

        var updated = rows.Select(row => row with { FullName = row.FullName + " المحدث" }).ToArray();
        Assert.Equal(250, store.ImportStudents(owner, school.Id, updated, "sample.xlsx"));
        var after = store.GetStudents(owner, school.Id, grade.Id);
        Assert.Equal(250, after.Count);
        Assert.All(after, student => Assert.Equal(identities[student.StudentCode], student.Id));
        Assert.All(after, student => Assert.EndsWith("المحدث", student.FullName));
        Assert.Equal(91, Assert.Single(store.GetStudentResults(owner, school.Id, first.Id)).Score);
    }

    [Fact]
    public void ImportObtainsStudentIdFromUpsertWithoutPerRowSelect()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SchoolObservatory.sln")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Solution not found");
        var source = File.ReadAllText(Path.Combine(directory.FullName, "src",
            "SchoolObservatory.Data", "SqliteStore.Students.cs"));
        Assert.Contains("RETURNING id", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using var find = Cmd(con, tx", source, StringComparison.Ordinal);
    }
}
