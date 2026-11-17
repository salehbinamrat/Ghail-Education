using System.Xml.Linq;
using SchoolObservatory.Core;
using Xunit;

namespace SchoolObservatory.Tests;

/// <summary>Test-first Phase 5 contracts. WPF interaction still needs the manual device matrix.</summary>
public sealed class ProfessionalUiPhase5Tests
{
    private static string PathInRepo(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null &&
               !File.Exists(Path.Combine(current.FullName, "SchoolObservatory.sln")))
            current = current.Parent;
        Assert.NotNull(current);
        return Path.Combine(new[] { current!.FullName }.Concat(segments).ToArray());
    }

    private static string Desktop(string file) =>
        File.ReadAllText(PathInRepo("src", "SchoolObservatory.Desktop", file));

    [Fact]
    public void AllEntryWindowsRemainArabicRightToLeftAndBoldArial()
    {
        foreach (var file in new[] { "MainWindow.xaml", "LoginWindow.xaml", "SetupWindow.xaml" })
        {
            var doc = XDocument.Load(PathInRepo("src", "SchoolObservatory.Desktop", file));
            Assert.Equal("RightToLeft", (string?)doc.Root?.Attribute("FlowDirection"));
            Assert.Equal("Arial", (string?)doc.Root?.Attribute("FontFamily"));
            Assert.Equal("Bold", (string?)doc.Root?.Attribute("FontWeight"));
        }
    }

    [Fact]
    public void ProfessionalThemeIsCentralizedAndMergedWithoutDiscardingExistingStyles()
    {
        var app = Desktop("App.xaml");
        Assert.Contains("Styles/ProfessionalTheme.xaml", app, StringComparison.Ordinal);
        var doc = XDocument.Load(PathInRepo("src", "SchoolObservatory.Desktop",
            "Styles", "ProfessionalTheme.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = doc.Descendants().Select(node => (string?)node.Attribute(x + "Key"))
            .Where(key => key is not null).ToHashSet();
        foreach (var name in new[] {
            "SurfaceBrush", "AccentBrush", "HeadingBrush", "ErrorBrush", "FocusBrush",
            "ProfessionalCard", "ProfessionalHeading", "ProfessionalHint",
            "InlineError", "ProfessionalEmptyState", "NavigationButton"
        })
            Assert.Contains(name, keys);
        Assert.Contains("Arial", doc.ToString(), StringComparison.Ordinal);
        Assert.Contains("Bold", doc.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardHasIncrementalMvvmWithOneMetricForEachMeaning()
    {
        var overview = DashboardOverview.Compose(
            new LiveDirectoryTotals(5, 100, 30, 4, 2, 9, 3, true, true,
                DirectStaff: 20, SecondedStaff: 10, UnclassifiedStaff: 6),
            new DashboardStats(5, 2, 8, 110, 31, 12));
        var keys = overview.Operational.Concat(overview.Approvals).Select(x => x.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(overview.Operational, x => x.Key == "active_students" && x.Value == 100);
        Assert.Contains(overview.Approvals, x => x.Key == "approved_submissions" && x.Value == 8);
        Assert.DoesNotContain(overview.Operational, x => x.Key == "approved_submissions");
        Assert.DoesNotContain(overview.Approvals, x => x.Key == "active_students");

        var restricted = DashboardOverview.Compose(
            new LiveDirectoryTotals(1, 1, 1, 1, 1, 1, 0, false, false),
            new DashboardStats(1, 0, 0, 0, 0, 0));
        Assert.DoesNotContain(restricted.Operational, x => x.Key == "active_students");
        Assert.DoesNotContain(restricted.Operational, x => x.Key == "active_teachers");
        var view = Desktop("Views/DashboardView.xaml");
        Assert.Contains("ItemsSource=\"{Binding Operational}\"", view, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Approvals}\"", view, StringComparison.Ordinal);
        Assert.Contains("LoadingMessage", view, StringComparison.Ordinal);
        var vm = Desktop("ViewModels/DashboardViewModel.cs");
        Assert.Contains("INotifyPropertyChanged", Desktop("ViewModels/ObservableViewModel.cs"), StringComparison.Ordinal);
        Assert.Contains("await Task.Run", vm, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1280, 1.0, false)]
    [InlineData(1024, 1.25, false)]
    [InlineData(1024, 1.5, true)]
    [InlineData(800, 1.5, true)]
    public void ScalingKeepsNavigationUsableAndAllowsCollapsing(double physicalWidth, double scale,
        bool expectedCompact)
    {
        var dipWidth = physicalWidth / scale;
        Assert.Equal(expectedCompact, ResponsiveSizing.ShouldCollapseSidebar(dipWidth));
        var sidebar = ResponsiveSizing.SidebarWidth(dipWidth);
        Assert.InRange(sidebar, 0, 246);
        Assert.True(sidebar == 0 || dipWidth - sidebar >= 500);
    }

    [Fact]
    public void NavigationRemembersOnlyExplicitNonSecretContext()
    {
        var context = new NavigationContext();
        context.Set("schools.search", "العليب");
        context.Set("students.search", "سالم");
        Assert.Equal("العليب", context.Get("schools.search"));
        Assert.Equal("سالم", context.Get("students.search"));
        Assert.Throws<ArgumentException>(() => context.Set("login.password", "unsafe"));
        context.Clear("schools.search");
        Assert.Equal("", context.Get("schools.search"));
        Assert.Equal("سالم", context.Get("students.search"));
    }

    [Fact]
    public void ShellProvidesKeyboardAccessibleCompactNavigation()
    {
        var xaml = Desktop("MainWindow.xaml");
        Assert.Contains("x:Name=\"SidebarColumn\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NavigationToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"إظهار أو إخفاء قائمة التنقل\"", xaml, StringComparison.Ordinal);
        var source = Desktop("MainWindow.xaml.cs");
        Assert.Contains("ResponsiveSizing.SidebarWidth", source, StringComparison.Ordinal);
        Assert.Contains("NavigationToggle_Click", source, StringComparison.Ordinal);
        Assert.Contains("AccessPolicy.Has(actor, PermissionFlags.ViewSchools)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SchoolFormUsesAccessibleInlineValidationAndKeepsSearchOnReturn()
    {
        var main = Desktop("MainWindow.xaml.cs");
        Assert.Contains("SchoolFormValidator.Validate", main, StringComparison.Ordinal);
        Assert.Contains("InlineValidationPresenter", main, StringComparison.Ordinal);
        Assert.Contains("NavigationContext", main, StringComparison.Ordinal);
        Assert.Contains("_navigationContext.Set(\"schools.search\"", main, StringComparison.Ordinal);
        Assert.Contains("_navigationContext.Get(\"schools.search\")", main, StringComparison.Ordinal);
        var fields = Desktop("MainWindow.Students.cs");
        Assert.Contains("AutomationProperties.SetLabeledBy", fields, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText", fields, StringComparison.Ordinal);
    }

    [Fact]
    public void TableSurfaceHasVisibleEmptyStateAndPreservesSortingAndVirtualization()
    {
        var main = Desktop("MainWindow.xaml.cs");
        Assert.Contains("private static Grid TableSurface", main, StringComparison.Ordinal);
        Assert.Contains("grid.HasItems", main, StringComparison.Ordinal);
        Assert.Contains("CanUserSortColumns = true", main, StringComparison.Ordinal);
        Assert.Contains("EnableRowVirtualization = true", main, StringComparison.Ordinal);
        Assert.Contains("TableSurface(grid", main, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedHistoricalAndImportPathsStillExist()
    {
        var main = Desktop("MainWindow.xaml.cs");
        Assert.Contains("ShowImportHistory", main, StringComparison.Ordinal);
        Assert.Contains("ShowHistoricalRosters", main, StringComparison.Ordinal);
        Assert.Contains("ShowReconciliation", main, StringComparison.Ordinal);
        var wizard = Desktop("MainWindow.ImportWizard.cs");
        Assert.Contains("await Task.Run", wizard, StringComparison.Ordinal);
        Assert.Contains("_store.ApplyImport", wizard, StringComparison.Ordinal);
        Assert.Contains("_store.ReverseImport", wizard, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualWindowsInteractionMatrixCoversDpiRolesAndEachDataWorkflow()
    {
        var doc = File.ReadAllText(PathInRepo("docs", "PHASE5_WPF_UX_MANUAL_TESTS.md"));
        foreach (var token in new[] {
            "100%", "125%", "150%", "1024", "1366", "1920",
            "لوحة التحكم", "المدارس", "الطلاب", "الكادر", "الإحصاء",
            "الاستيراد", "الصلاحيات", "لوحة المفاتيح", "التراجع"
        })
            Assert.Contains(token, doc, StringComparison.Ordinal);
    }

    [Fact]
    public void XamlNamedEventHandlersStayConnectedToActualCodeBehind()
    {
        foreach (var page in new[] {
            "MainWindow", "LoginWindow", "SetupWindow", "Views/DashboardView"
        })
        {
            var xaml = XDocument.Load(PathInRepo("src", "SchoolObservatory.Desktop",
                page.Replace('/', Path.DirectorySeparatorChar) + ".xaml"));
            var code = Desktop(page + ".xaml.cs");
            foreach (var control in xaml.Descendants())
            {
                foreach (var evt in new[] { "Click", "Loaded", "KeyDown" })
                {
                    var handler = (string?)control.Attribute(evt);
                    if (handler is not null)
                        Assert.Contains(handler + "(", code, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void ReviewAndCensusActionsAreNotShownOutsideTheirPermissions()
    {
        var source = Desktop("MainWindow.xaml.cs");
        Assert.Contains("if (AccessPolicy.Has(_actor, PermissionFlags.ApproveDistrict))",
            source, StringComparison.Ordinal);
        Assert.Contains("if (AccessPolicy.Has(_actor, PermissionFlags.ApproveSchool))",
            source, StringComparison.Ordinal);
        Assert.Contains("if (editable) form.Children.Add(actions)", source, StringComparison.Ordinal);
        Assert.Contains("new UiValidationIssue(\"reason\"", source, StringComparison.Ordinal);
        var students = Desktop("MainWindow.Students.cs");
        Assert.Contains("exportRoster.IsEnabled = canManage;", students, StringComparison.Ordinal);
        Assert.Contains("printRoster.IsEnabled = canManage;", students, StringComparison.Ordinal);
    }

    [Fact]
    public void SchoolUnsavedDraftSurvivesNavigationButNeverWritesUntilSave()
    {
        var source = Desktop("MainWindow.xaml.cs");
        Assert.Contains("schools.draft.exists", source, StringComparison.Ordinal);
        Assert.Contains("schools.draft.code", source, StringComparison.Ordinal);
        Assert.Contains("schools.draft.name", source, StringComparison.Ordinal);
        Assert.Contains("schools.draft.zone", source, StringComparison.Ordinal);
        Assert.Contains("code.TextChanged += (_, _) => SaveSchoolDraft()", source, StringComparison.Ordinal);
        Assert.Contains("ClearPersistedSchoolDraft()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_store.AddSchool(_actor", source[
            source.IndexOf("void SaveSchoolDraft()", StringComparison.Ordinal)..
            source.IndexOf("void ClearPersistedSchoolDraft()", StringComparison.Ordinal)],
            StringComparison.Ordinal);
    }
}
