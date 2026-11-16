using System.Xml.Linq;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class FirstRunLoginTests
{
    private static string FreshDatabasePath() =>
        Path.Combine(Path.GetTempPath(), "school-first-run-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public void FreshInstallRequiresOwnerBeforeLogin_AndReopenSkipsSetup()
    {
        var path = FreshDatabasePath();
        var first = new SqliteStore(path);
        first.Initialize();
        Assert.False(first.HasUsers());
        first.InitializeAdministrator("غيل بن يمين", "2026/2027", "owner1", "Strong-Owner-Password-123!");

        var reopened = new SqliteStore(path);
        reopened.Initialize();
        Assert.True(reopened.HasUsers());
        Assert.Null(reopened.Authenticate("owner1", "Incorrect-Password"));
        var owner = Assert.IsType<UserAccount>(reopened.Authenticate("OWNER1", "Strong-Owner-Password-123!"));
        Assert.True(owner.IsOwner);
        Assert.Equal("owner1", owner.Username, ignoreCase: true);
        Assert.Throws<InvalidOperationException>(() =>
            reopened.InitializeAdministrator("مديرية أخرى", "2026/2027", "other1", "Strong-Other-Password-123!"));
    }

    [Fact]
    public void DelegatedAccountCanSignInButWrongPasswordIsRejected()
    {
        var path = FreshDatabasePath();
        var store = new SqliteStore(path);
        store.Initialize();
        store.InitializeAdministrator("غيل بن يمين", "2026/2027", "owner1", "Strong-Owner-Password-123!");
        var owner = Assert.IsType<UserAccount>(store.Authenticate("owner1", "Strong-Owner-Password-123!"));
        store.CreateUser(owner, "auditor1", "Strong-Auditor-Password-123!", Roles.Reviewer, null);
        Assert.Null(store.Authenticate("auditor1", "wrong"));
        var auditor = Assert.IsType<UserAccount>(store.Authenticate("auditor1", "Strong-Auditor-Password-123!"));
        Assert.Equal(Roles.Reviewer, auditor.Role);
        Assert.False(auditor.IsOwner);
    }

    [Fact]
    public void SetupFooterAndLoginButtonStayOutsideScrollableForm()
    {
        var doc = XDocument.Load(SourcePath("src", "SchoolObservatory.Desktop", "SetupWindow.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var buttons = doc.Descendants(wpf + "Button").ToArray();
        var create = Assert.Single(buttons.Where(b => (string?)b.Attribute(x + "Name") == "CreateAdministratorButton"));
        var login = Assert.Single(buttons.Where(b => (string?)b.Attribute(x + "Name") == "ExistingLoginButton"));
        Assert.Empty(create.Ancestors(wpf + "ScrollViewer"));
        Assert.Empty(login.Ancestors(wpf + "ScrollViewer"));
        Assert.Contains(doc.Descendants(wpf + "ScrollViewer"), v =>
            (string?)v.Attribute(x + "Name") == "SetupFieldsScroll");
    }

    [Fact]
    public void LoginPageHasAccessibleSubmitAction()
    {
        var doc = XDocument.Load(SourcePath("src", "SchoolObservatory.Desktop", "LoginWindow.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Contains(doc.Descendants(wpf + "Button"), b =>
            (string?)b.Attribute(x + "Name") == "LoginButton" &&
            (string?)b.Attribute("Click") == "Login_Click");
        Assert.Contains(doc.Descendants(wpf + "PasswordBox"), p =>
            (string?)p.Attribute(x + "Name") == "PasswordInput");
    }

    private static string SourcePath(params string[] relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SchoolObservatory.sln")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository root not found.");
        return Path.Combine(new[] { directory.FullName }.Concat(relative).ToArray());
    }
}
