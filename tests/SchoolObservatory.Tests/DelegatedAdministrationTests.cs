using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class DelegatedAdministrationTests
{
    private static (SqliteStore Db, UserAccount Owner) CreateDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), "school-permissions-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Strong-Owner-Password-123!");
        var owner = Assert.IsType<UserAccount>(db.Authenticate("founder", "Strong-Owner-Password-123!"));
        return (db, owner);
    }

    [Fact]
    public void FirstAccountIsProtectedOwner_AndSetupCannotBeRepeated()
    {
        var (db, owner) = CreateDatabase();
        Assert.True(owner.IsOwner);
        Assert.True(AccessPolicy.CanCreateUsers(owner));
        Assert.Throws<InvalidOperationException>(() =>
            db.InitializeAdministrator("أخرى", "2026/2027", "another", "Strong-Other-Password-123!"));
        Assert.Throws<InvalidOperationException>(() => db.SetUserActive(owner, owner.Id, false));
        Assert.Throws<InvalidOperationException>(() => db.UpdateUserPermissions(owner, owner.Id, (long)PermissionFlags.ViewSchools));
    }

    [Fact]
    public void OwnerCanCreateRestrictedAdministratorAndPromoteSpecificRights()
    {
        var (db, owner) = CreateDatabase();
        var minimal = PermissionFlags.ViewSchools | PermissionFlags.ViewReports;
        db.CreateUser(owner, "admin2", "Strong-Admin-Password-123!", Roles.Admin, null, (long)minimal);
        var delegated = Assert.IsType<UserAccount>(db.Authenticate("admin2", "Strong-Admin-Password-123!"));
        Assert.False(delegated.IsOwner);
        Assert.True(AccessPolicy.CanSeeSchool(delegated, "any"));
        Assert.False(AccessPolicy.CanEditSchool(delegated));
        Assert.False(AccessPolicy.CanCreateUsers(delegated));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.AddSchool(delegated, "S-100", "مدرسة", "العليب", "حكومية", "أساسي"));
        db.UpdateUserPermissions(owner, delegated.Id, (long)(minimal | PermissionFlags.ManageSchools));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.AddSchool(delegated, "S-100", "مدرسة", "العليب", "حكومية", "أساسي"));
        var promoted = Assert.IsType<UserAccount>(db.Authenticate("admin2", "Strong-Admin-Password-123!"));
        db.AddSchool(promoted, "S-100", "مدرسة", "العليب", "حكومية", "أساسي");
        Assert.Single(db.GetSchools(owner));
    }

    [Fact]
    public void DelegatedUserManagerCannotCreateAdminOrEscalateRights()
    {
        var (db, owner) = CreateDatabase();
        var manage = PermissionFlags.ViewSchools | PermissionFlags.ManageUsers;
        db.CreateUser(owner, "manager1", "Strong-Manager-Password-123!", Roles.Admin, null, (long)manage);
        var manager = Assert.IsType<UserAccount>(db.Authenticate("manager1", "Strong-Manager-Password-123!"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.CreateUser(manager, "otheradm", "Strong-Admin-Password-123!", Roles.Admin, null,
                (long)PermissionFlags.ViewSchools));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.CreateUser(manager, "statistic01", "Strong-Review-Password-123!", Roles.Statistician, null,
                (long)(PermissionFlags.ViewSchools | PermissionFlags.ApproveDistrict)));
        db.CreateUser(manager, "viewer01", "Strong-Viewer-Password-123!", Roles.Viewer, null,
            (long)PermissionFlags.ViewSchools);
        var viewer = Assert.IsType<UserAccount>(db.Authenticate("viewer01", "Strong-Viewer-Password-123!"));
        Assert.False(AccessPolicy.CanEditSchool(viewer));
        Assert.Throws<UnauthorizedAccessException>(() => db.UpdateUserPermissions(manager, owner.Id,
            (long)PermissionFlags.ViewSchools));
        db.CreateUser(owner, "senior", "Strong-Senior-Password-123!", Roles.Statistician, null,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ManageSchools));
        var senior = Assert.IsType<UserAccount>(db.Authenticate("senior", "Strong-Senior-Password-123!"));
        Assert.Throws<UnauthorizedAccessException>(() => db.SetUserActive(manager, senior.Id, false));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.ResetUserPassword(manager, senior.Id, "Strong-Replacement-456!"));
    }

    [Fact]
    public void RevokingRightsOrDisablingAccountInvalidatesPreviousSession()
    {
        var (db, owner) = CreateDatabase();
        db.CreateUser(owner, "helper", "Strong-Helper-Password-123!", Roles.Admin, null,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ManageSchools));
        var user = Assert.IsType<UserAccount>(db.Authenticate("helper", "Strong-Helper-Password-123!"));
        db.UpdateUserPermissions(owner, user.Id, (long)PermissionFlags.ViewSchools);
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.AddSchool(user, "S-200", "مدرسة", "العليب", "حكومية", "أساسي"));
        var refreshed = Assert.IsType<UserAccount>(db.Authenticate("helper", "Strong-Helper-Password-123!"));
        Assert.False(AccessPolicy.CanEditSchool(refreshed));
        db.SetUserActive(owner, user.Id, false);
        Assert.Null(db.Authenticate("helper", "Strong-Helper-Password-123!"));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetSchools(refreshed));
    }

    [Fact]
    public void AuthorizedPasswordResetRevokesOldCredentialAndCreatesAudit()
    {
        var (db, owner) = CreateDatabase();
        db.CreateUser(owner, "account9", "Strong-Original-123!", Roles.Reviewer, null,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ReviewCensus));
        var target = Assert.IsType<UserAccount>(db.Authenticate("account9", "Strong-Original-123!"));
        db.ResetUserPassword(owner, target.Id, "Strong-Replacement-456!");
        Assert.Throws<UnauthorizedAccessException>(() => db.GetSchools(target));
        Assert.Null(db.Authenticate("account9", "Strong-Original-123!"));
        Assert.NotNull(db.Authenticate("account9", "Strong-Replacement-456!"));
        Assert.Contains(db.GetRecentAudit(owner), a => a.Action == "user.password_reset");
    }
    [Fact]
    public void ExistingLegacyAdministratorIsMigratedToProtectedOwnerWithoutLosingCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), "school-legacy-" + Guid.NewGuid().ToString("N") + ".db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (
                  id TEXT PRIMARY KEY, username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                  password_hash TEXT NOT NULL, role TEXT NOT NULL, school_id TEXT NULL,
                  is_active INTEGER NOT NULL DEFAULT 1, failed_attempts INTEGER NOT NULL DEFAULT 0,
                  lock_until_utc TEXT NULL, created_utc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO users(id,username,password_hash,role,school_id,created_utc)
                VALUES('legacy-founder','legacyroot',$password,'admin',NULL,'2026-10-01T10:00:00Z');
                """;
            insert.Parameters.AddWithValue("$password", PasswordService.Hash("Strong-Legacy-Password-123!"));
            insert.ExecuteNonQuery();
        }

        var db = new SqliteStore(path);
        db.Initialize();
        db.Initialize();
        var legacy = Assert.IsType<UserAccount>(db.Authenticate("legacyroot", "Strong-Legacy-Password-123!"));
        Assert.True(legacy.IsOwner);
        Assert.True(AccessPolicy.CanCreateUsers(legacy));
        Assert.Equal("legacy-founder", legacy.Id);
        Assert.Throws<InvalidOperationException>(() =>
            db.InitializeAdministrator("المديرية", "2026/2027", "newowner", "Strong-New-Password-123!"));
    }

    [Fact]
    public void RoleCannotReceiveIncompatibleAdministrativePermissions()
    {
        var (db, owner) = CreateDatabase();
        Assert.False(PermissionCatalog.IsRoleAllowed(Roles.Viewer,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ApproveDistrict)));
        Assert.Throws<ArgumentException>(() =>
            db.CreateUser(owner, "viewer2", "Strong-Viewer-Password-123!", Roles.Viewer, null,
                (long)(PermissionFlags.ViewSchools | PermissionFlags.ApproveDistrict)));
    }

}
