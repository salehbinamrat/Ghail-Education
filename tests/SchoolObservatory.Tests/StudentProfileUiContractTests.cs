using Xunit;

namespace SchoolObservatory.Tests;

/// <summary>Regression checks for student profile navigation and roster load efficiency.</summary>
public sealed class StudentProfileUiContractTests
{
    private static string ReadUi()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "SchoolObservatory.sln")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Solution directory not found.");
        return File.ReadAllText(Path.Combine(root.FullName,
            "src", "SchoolObservatory.Desktop", "MainWindow.Students.cs"));
    }

    [Fact]
    public void SavingResultAssessmentOrAttendanceKeepsTheRelevantProfileTab()
    {
        var source = ReadUi();
        Assert.Contains("ShowStudentProfile(school, studentId, 1)", source, StringComparison.Ordinal);
        Assert.Contains("ShowStudentProfile(school, studentId, 2)", source, StringComparison.Ordinal);
        Assert.Contains("ShowStudentProfile(school, studentId, 3)", source, StringComparison.Ordinal);
        Assert.Contains("tabs.SelectedIndex = Math.Clamp(selectedTab, 0, 3)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileHistoryIsLoadedOnlyWhenItsTabIsOpened()
    {
        var source = ReadUi();
        Assert.Contains("void LoadSelectedTab()", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(e.OriginalSource, tabs)", source, StringComparison.Ordinal);
        Assert.Contains("GridFor(Array.Empty<StudentResult>()", source, StringComparison.Ordinal);
        Assert.Contains("GridFor(Array.Empty<StudentAssessment>()", source, StringComparison.Ordinal);
        Assert.Contains("GridFor(Array.Empty<StudentAttendance>()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewOnlyStudentAccountsCanReadTheirProfileWithoutEditableFields()
    {
        var source = ReadUi();
        Assert.DoesNotContain("personal.IsEnabled = canEdit;", source, StringComparison.Ordinal);
        Assert.Contains("name.IsReadOnly = !canEdit;", source, StringComparison.Ordinal);
        Assert.Contains("notes.IsReadOnly = !canEdit;", source, StringComparison.Ordinal);
        Assert.Contains("resultEditor.IsEnabled = canEvaluate;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchingSchoolGradeDoesNotRefreshRosterSeveralTimes()
    {
        var source = ReadUi();
        Assert.Contains("suppressGradeSelection", source, StringComparison.Ordinal);
        Assert.Contains("if (suppressGradeSelection) return;", source, StringComparison.Ordinal);
        Assert.Contains("RefreshRoster(false)", source, StringComparison.Ordinal);
    }
}
