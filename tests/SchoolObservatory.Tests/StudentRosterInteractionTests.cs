using SchoolObservatory.Core;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class StudentRosterInteractionTests
{
    private static SchoolStudent Student(string code, string name, string guardian) =>
        new(code, "school-1", code, name, 3, "أ", "ذكور", guardian,
            "", "", true, "2026/2027");

    [Fact]
    public void SearchIsLocalAndSupportsStudentCodeNameAndGuardian()
    {
        var students = new[]
        {
            Student("0001", "علي سالم", "سالم محمد"),
            Student("0002", "خالد حسن", "صالح محمد"),
            Student("0003", "أحمد صالح", "علي عمر")
        };
        Assert.Same(students, SchoolStudentSearch.Find(students, "  "));
        Assert.Equal("0001", Assert.Single(SchoolStudentSearch.Find(students, "0001")).StudentCode);
        Assert.Equal("0002", Assert.Single(SchoolStudentSearch.Find(students, "خالد")).StudentCode);
        Assert.Equal(new[] { "0001", "0003" },
            SchoolStudentSearch.Find(students, "علي").Select(x => x.StudentCode).ToArray());
        Assert.Empty(SchoolStudentSearch.Find(students, "غير موجود"));
    }

    private static string ReadProjectSource(string filename = "MainWindow.Students.cs")
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SchoolObservatory.sln")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Solution root missing.");
        return File.ReadAllText(Path.Combine(directory.FullName,
            "src", "SchoolObservatory.Desktop", filename));
    }

    [Fact]
    public void ClassSelectionDoesNotNavigateAsSideEffectOfGridSelection()
    {
        var source = ReadProjectSource();
        Assert.DoesNotContain("roster.SelectionChanged += (_, _) =>", source, StringComparison.Ordinal);
        Assert.Contains("roster.MouseDoubleClick +=", source, StringComparison.Ordinal);
        Assert.Contains("فتح ملف الطالب المحدد", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassRosterCannotSilentlyShowAllSchoolStudentsWithoutSelectedGrade()
    {
        var source = ReadProjectSource();
        Assert.Contains("if (selectedSchool is null || selectedGrade is null)", source,
            StringComparison.Ordinal);
        Assert.Contains("_store.GetStudents(_actor, selectedSchool.Id, selectedGrade.Id)", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_store.GetStudents(_actor, selectedSchool.Id, selectedGrade?.Id)", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClassRosterSupportsOfficialPrintableDocumentWithSchoolAndYear()
    {
        var source = ReadProjectSource();
        Assert.Contains("طباعة كشف الصف", source, StringComparison.Ordinal);
        Assert.Contains("new FlowDocument", source, StringComparison.Ordinal);
        Assert.Contains("PrintDocument(", source, StringComparison.Ordinal);
        Assert.Contains("العام الدراسي", source, StringComparison.Ordinal);
        Assert.Contains("printRoster.IsEnabled = editor.IsEnabled", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StudentImportDisablesRepeatedExecutionAndRestoresControls()
    {
        var studentPage = ReadProjectSource();
        var wizard = ReadProjectSource("MainWindow.ImportWizard.cs");
        Assert.Contains("ShowImportWizard(ImportEntity.Students", studentPage, StringComparison.Ordinal);
        Assert.Contains("if (importBusy) return false;", wizard, StringComparison.Ordinal);
        Assert.Contains("importBusy = true;", wizard, StringComparison.Ordinal);
        Assert.Contains("importBusy = false;", wizard, StringComparison.Ordinal);
        Assert.Contains("category.IsEnabled = false;", wizard, StringComparison.Ordinal);
        Assert.Contains("category.IsEnabled = true;", wizard, StringComparison.Ordinal);
        Assert.Contains("finally", wizard, StringComparison.Ordinal);
    }
}
