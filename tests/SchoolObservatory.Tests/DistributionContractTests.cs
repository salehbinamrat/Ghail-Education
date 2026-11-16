using Xunit;

namespace SchoolObservatory.Tests;

/// <summary>Guards the user-facing four-download contract against accidental CI regressions.</summary>
public sealed class DistributionContractTests
{
    private static string RootFile(params string[] relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SchoolObservatory.sln")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Solution root not found.");
        return Path.Combine(new[] { directory.FullName }.Concat(relative).ToArray());
    }

    [Fact]
    public void ReadmeHasFourIndependentDirectDownloadLinks()
    {
        var text = File.ReadAllText(RootFile("README.md"));
        foreach (var arch in new[] { "x86", "x64" })
        {
            foreach (var kind in new[] { "Portable.zip", "Installer.exe" })
            {
                Assert.Contains(
                    $"/releases/latest/download/SchoolObservatory-{arch}-{kind}",
                    text, StringComparison.Ordinal);
            }
        }
        Assert.Contains("نسخ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsReleaseBuildsAndUploadsEachFileSeparatelyAfterTests()
    {
        var workflow = File.ReadAllText(RootFile(".github", "workflows", "windows-release.yml"));
        Assert.Contains("needs: release-tests", workflow, StringComparison.Ordinal);
        Assert.Contains("matrix:", workflow, StringComparison.Ordinal);
        Assert.Contains("arch: [x86, x64]", workflow, StringComparison.Ordinal);
        Assert.Contains("name: school-observatory-${{ matrix.arch }}-portable", workflow, StringComparison.Ordinal);
        Assert.Contains("name: school-observatory-${{ matrix.arch }}-installer", workflow, StringComparison.Ordinal);
        Assert.Contains("path: artifacts/packages/SchoolObservatory-${{ matrix.arch }}-Portable.zip", workflow, StringComparison.Ordinal);
        Assert.Contains("path: artifacts/packages/SchoolObservatory-${{ matrix.arch }}-Installer.exe", workflow, StringComparison.Ordinal);
        Assert.Contains("Release / verify-all-4", workflow, StringComparison.Ordinal);
        Assert.Contains("if ((Get-ChildItem release -File).Count -ne 4)", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicationRequiresSameMainCommitBothWorkflowsSucceeded()
    {
        var workflow = File.ReadAllText(RootFile(".github", "workflows", "publish-downloads.yml"));
        Assert.Contains("workflows: [\"Windows Core\", \"Windows Release\"]", workflow, StringComparison.Ordinal);
        Assert.Contains("head_branch == 'main'", workflow, StringComparison.Ordinal);
        Assert.Contains("head_repository.full_name == github.repository", workflow, StringComparison.Ordinal);
        Assert.Contains("main\" != \"$HEAD_SHA\"", workflow, StringComparison.Ordinal);
        Assert.Contains("status\" != \"completed\" || \"$conclusion\" != \"success\"", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release create", workflow, StringComparison.Ordinal);
        Assert.Contains("--latest", workflow, StringComparison.Ordinal);
    }
}
