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
    public void PublishedPhaseOneReleaseNotesMustDescribeEncryptedStorage()
    {
        var workflow = File.ReadAllText(RootFile(".github", "workflows", "publish-downloads.yml"));
        Assert.Contains("SQLCipher", workflow, StringComparison.Ordinal);
        Assert.Contains("نسخة احتياطية مشفرة", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("قاعدة البيانات المحلية غير مشفرة", workflow, StringComparison.Ordinal);
    }


    [Fact]
    public void WindowsReleaseRunsNativeX86TestsUsingAnIsolated32BitDotnetHost()
    {
        var workflow = File.ReadAllText(RootFile(".github", "workflows", "windows-release.yml"));
        // The runner's normal DOTNET_ROOT refers to x64. An x86 testhost cannot
        // load its hostfxr.dll (0x800700C1) unless a separate x86 runtime exists.
        Assert.Contains("architecture: x86", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet-x86", workflow, StringComparison.Ordinal);
        Assert.Contains(@"Join-Path $env:RUNNER_TEMP 'dotnet-x86\x86'", workflow, StringComparison.Ordinal);
        Assert.Contains("$env:DOTNET_ROOT_X86 = $x86Root", workflow, StringComparison.Ordinal);
        Assert.Contains("Join-Path $x86Root 'dotnet.exe'", workflow, StringComparison.Ordinal);
        Assert.Contains("SCHOOL_OBSERVATORY_EXPECTED_ARCH = 'x86'", workflow, StringComparison.Ordinal);
        Assert.Contains("SCHOOL_OBSERVATORY_EXPECTED_ARCH = 'x64'", workflow, StringComparison.Ordinal);
        Assert.Contains("needs: release-tests", workflow, StringComparison.Ordinal);
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
