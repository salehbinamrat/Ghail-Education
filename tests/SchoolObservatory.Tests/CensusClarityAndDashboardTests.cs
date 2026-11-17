using System.Globalization;
using SchoolObservatory.Core;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class CensusClarityAndDashboardTests
{
    [Theory]
    [InlineData("20", 20)]
    [InlineData("٢٠", 20)]
    [InlineData("۲۰", 20)]
    [InlineData("  ١٢٣  ", 123)]
    [InlineData("۰", 0)]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    public void CensusNumbersAcceptEnglishArabicPersianDigitsAndOptionalBlank(string value, int expected)
    {
        Assert.Equal(expected, CensusNumberInput.ParseNonNegative(value, "الشعب الدراسية"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("١-٢")]
    [InlineData("1.5")]
    [InlineData("١٫٥")]
    [InlineData("اثنان")]
    [InlineData("2147483648")]
    public void CensusNumbersRejectInvalidOrOverflowWithoutFakingTotals(string value)
    {
        Assert.Throws<ArgumentException>(() => CensusNumberInput.ParseNonNegative(value, "الشعب الدراسية"));
    }

    [Fact]
    public void RegisteredSectionsAndAcademicYearCanBeCountedWithoutGuessingClassrooms()
    {
        var sections = new[]
        {
            new SchoolGrade("a", "s", "2026/2027", 10, "أ", "ثانوي"),
            new SchoolGrade("b", "s", "2026/2027", 10, "ب", "ثانوي"),
            new SchoolGrade("c", "s", "2026/2027", 11, "أ", "ثانوي")
        };
        Assert.Equal(3, CensusNumberInput.CountRegisteredSections(sections));
        Assert.Equal(0, CensusNumberInput.CountRegisteredSections(Array.Empty<SchoolGrade>()));
    }

    [Fact]
    public void StudentSuggestionCountsEachActiveEnrolledStudentOncePerGradeAndGender()
    {
        var students = new[]
        {
            new SchoolStudent("1", "s", "A1", "طالب أ", 10, "أ", "ذكور", "", "", "", true, "2026/2027"),
            new SchoolStudent("2", "s", "A2", "طالب ب", 10, "ب", "ذكور", "", "", "", true, "2026/2027"),
            new SchoolStudent("3", "s", "A3", "طالبة", 10, "أ", "إناث", "", "", "", true, "2026/2027"),
            new SchoolStudent("4", "s", "A4", "مؤرشف", 10, "أ", "ذكور", "", "", "", false, "2026/2027"),
            new SchoolStudent("5", "s", "A5", "عام قديم", 10, "أ", "ذكور", "", "", "", true, "2025/2026"),
            new SchoolStudent("6", "s", "A6", "طالب ثاني", 11, "أ", "ذكور", "", "", "", true, "2026/2027")
        };
        var result = CensusNumberInput.SuggestGradeTotals(students, "2026/2027");
        Assert.Equal(2, result[10].Boys);
        Assert.Equal(1, result[10].Girls);
        Assert.Equal(1, result[11].Boys);
        Assert.DoesNotContain(9, result.Keys);
    }

    [Fact]
    public void ScreenshotRegressionDashboardHasOneSchoolsCardAndCensusExplainsSections()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SchoolObservatory.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var code = File.ReadAllText(Path.Combine(dir!.FullName,
            "src", "SchoolObservatory.Desktop", "MainWindow.xaml.cs"));
        var start = code.IndexOf("private void ShowDashboard()", StringComparison.Ordinal);
        var end = code.IndexOf("private static bool IsDoubleClickOnGridRow", start, StringComparison.Ordinal);
        var dashboard = code[start..end];
        var projection = File.ReadAllText(Path.Combine(dir.FullName,
            "src", "SchoolObservatory.Core", "Phase5UiModels.cs"));
        var view = File.ReadAllText(Path.Combine(dir.FullName,
            "src", "SchoolObservatory.Desktop", "Views", "DashboardView.xaml"));
        Assert.Contains("DashboardViewModel", dashboard, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(projection, "\"المدارس النشطة\""));
        Assert.Contains("حالة الاعتماد الرسمي", view, StringComparison.Ordinal);
        Assert.DoesNotContain("طلاب الإحصاء المعتمد", view, StringComparison.Ordinal);
        Assert.Contains("CensusNumberInput.ParseNonNegative", code, StringComparison.Ordinal);
        Assert.Contains("الشعب الدراسية", code, StringComparison.Ordinal);
        Assert.Contains("تعبئة عدد الشعب من كشوف المدرسة", code, StringComparison.Ordinal);
        Assert.Contains("ShowCensusCore(selected.Id)", code, StringComparison.Ordinal);
        Assert.Contains("void ValidateCensus(Action action)", code, StringComparison.Ordinal);
        Assert.Contains("ContentScroll.ScrollToTop()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PagesReturnToTheTopOnNavigationRatherThanOpeningHalfwayDown()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SchoolObservatory.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var folder = Path.Combine(dir!.FullName, "src", "SchoolObservatory.Desktop");
        var xaml = File.ReadAllText(Path.Combine(folder, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(folder, "MainWindow.xaml.cs"));
        Assert.Contains("x:Name=\"ContentScroll\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ContentScroll.ScrollToTop()", code, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string text) =>
        (value.Length - value.Replace(text, "", StringComparison.Ordinal).Length) / text.Length;
}
