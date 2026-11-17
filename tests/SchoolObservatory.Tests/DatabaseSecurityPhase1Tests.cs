using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class DatabaseSecurityPhase1Tests
{
    private static string PathFor() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "education-phase1-" + Guid.NewGuid().ToString("N"), "observatory.db");

    private static (SqliteStore Db, UserAccount Owner, string Path) Build()
    {
        var path = PathFor();
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("مديرية غيل بن يمين", "2026/2027", "phaseowner", "Good-Long-Password-123");
        return (db, db.Authenticate("phaseowner", "Good-Long-Password-123")!, path);
    }

    [Fact]
    public void FreshDatabaseIsEncryptedAtRestAndReopensWithoutDataLoss()
    {
        var (db, owner, path) = Build();
        db.AddSchool(owner, "SCH01", "مدرسة الاختبار", "العليب", "حكومية", "أساسي");
        Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 16));
        var reopened = new SqliteStore(path);
        reopened.Initialize();
        var login = reopened.Authenticate("phaseowner", "Good-Long-Password-123");
        Assert.NotNull(login);
        Assert.Single(reopened.GetSchools(login!));
    }

    [Fact]
    public void WrongDatabaseKeyCannotBeAcceptedOrReplaceAnExistingDatabase()
    {
        var (db, _, path) = Build();
        var original = SHA256.HashData(File.ReadAllBytes(path));
        Assert.ThrowsAny<Exception>(() => DatabaseSecurity.VerifyWithKey(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void PlaintextMigrationKeepsAllLegacyRowsAndOriginalRecoveryCopy()
    {
        var path = PathFor();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            con.Open();
            using var command = con.CreateCommand();
            command.CommandText = "CREATE TABLE legacy_records(id INTEGER PRIMARY KEY, value TEXT); INSERT INTO legacy_records VALUES(1,'school/student/teacher/results/permissions/audit');";
            command.ExecuteNonQuery();
        }
        var db = new SqliteStore(path);
        db.Initialize();
        Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 16));
        // The plaintext rollback must not persist after a verified successful migration.
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!, "*.legacy-original"));
        var recovery = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "recovery");
        var snapshots = Directory.GetFiles(recovery, "before-encryption-*.db");
        Assert.NotEmpty(snapshots);
        Assert.All(snapshots, snapshot =>
            Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(File.ReadAllBytes(snapshot), 0, 16)));
        Assert.Equal(1, DatabaseSecurity.CountRows(path, "legacy_records"));
    }

    [Fact]
    public void EncryptedBackupRestoresCompleteRepresentativeRecords()
    {
        var (db, owner, path) = Build();
        db.AddSchool(owner, "SCH01", "مدرسة الأمل", "العليب", "حكومية", "أساسي");
        var school = Assert.Single(db.GetSchools(owner));
        db.CreateUser(owner, "schooluser", "School-Password-987", Roles.Entry, school.Id);
        db.SaveDraft(owner, school.Id, new CensusData([new EnrollmentLine("1", 18, 17)], 3, 4, 2, "original"));
        var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "backup.sobak");
        var phrase = "A strong offline recovery secret number 554433!";
        db.CreateEncryptedBackup(owner, backup, phrase);
        Assert.True(File.Exists(backup));
        Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(File.ReadAllBytes(backup), 0, 16));
        var preview = db.PreviewEncryptedBackup(owner, backup, phrase);
        Assert.True(preview.RowCounts["schools"] >= 1);
        Assert.True(preview.RowCounts["users"] >= 2);
        db.RestoreEncryptedBackup(owner, backup, phrase, confirmed: true);
        var reopened = new SqliteStore(path);
        reopened.Initialize();
        var restoredOwner = reopened.Authenticate("phaseowner", "Good-Long-Password-123")!;
        Assert.Single(reopened.GetSchools(restoredOwner));
        Assert.NotNull(reopened.Authenticate("schooluser", "School-Password-987"));
        Assert.NotNull(reopened.GetLatestSubmission(restoredOwner, school.Id));
    }

    [Fact]
    public void WrongBackupPassphraseCannotCorruptActiveDatabase()
    {
        var (db, owner, path) = Build();
        var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "backup.sobak");
        db.CreateEncryptedBackup(owner, backup, "A strong offline recovery secret number 554433!");
        var original = SHA256.HashData(File.ReadAllBytes(path));
        Assert.ThrowsAny<Exception>(() => db.PreviewEncryptedBackup(owner, backup, "incorrect password"));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void MissingDpapiKeyNeverBootstrapsOverExistingEncryptedDatabase()
    {
        var (_, _, path) = Build();
        var key = path + ".key.dpapi";
        Assert.True(File.Exists(key));
        File.Move(key, key + ".detached");
        try { Assert.ThrowsAny<Exception>(() => new SqliteStore(path)); }
        finally { File.Move(key + ".detached", key); }
    }
}
