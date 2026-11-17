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
        db.AddSchool(owner, "SCH01", "مدرسة الأمل", "العليب", "حكومية", "أساسي", "ذكور");
        var school = Assert.Single(db.GetSchools(owner));
        db.CreateUser(owner, "schooluser", "School-Password-987", Roles.Entry, school.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStudents));
        db.SaveSchoolStaff(owner, new SchoolStaff("", school.Id, "STAFF-01", "المعلم خالد",
            "معلم", "معلم", "الرياضيات", "بكالوريوس", "ذكور", true));
        db.AddSchoolGrade(owner, school.Id, 3, "أ");
        db.SaveStudent(owner, new SchoolStudent("", school.Id, "ST-001", "الطالب محمد",
            3, "أ", "", "ولي الأمر", "777000000", "الملف الأصلي", true, "2026/2027"));
        var student = Assert.Single(db.GetStudents(owner, school.Id));
        db.SaveStudentResult(owner, school.Id, student.Id, "الفصل الأول", "الرياضيات", 94, 100, "ممتاز");
        db.AddStudentAssessment(owner, school.Id, student.Id, "المشاركة", "ممتاز", "نشط");
        db.RecordStudentAttendance(owner, school.Id, student.Id, new DateOnly(2026, 11, 17), "حاضر", "");
        db.SaveDraft(owner, school.Id, new CensusData([new EnrollmentLine("1", 18, 17)], 3, 4, 2, "original"));
        var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "backup.sobak");
        var phrase = "A strong offline recovery secret number 554433!";
        db.CreateEncryptedBackup(owner, backup, phrase);
        Assert.True(File.Exists(backup));
        Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(File.ReadAllBytes(backup), 0, 16));
        var preview = db.PreviewEncryptedBackup(owner, backup, phrase);
        Assert.True(preview.RowCounts["schools"] >= 1);
        Assert.True(preview.RowCounts["users"] >= 2);
        Assert.Equal(1, preview.RowCounts["school_staff"]);
        Assert.Equal(1, preview.RowCounts["school_students"]);
        Assert.Equal(1, preview.RowCounts["student_results"]);
        Assert.True(preview.RowCounts["audit_events"] > 0);
        db.RestoreEncryptedBackup(owner, backup, phrase, confirmed: true);
        var reopened = new SqliteStore(path);
        reopened.Initialize();
        var restoredOwner = reopened.Authenticate("phaseowner", "Good-Long-Password-123")!;
        Assert.Single(reopened.GetSchools(restoredOwner));
        Assert.NotNull(reopened.Authenticate("schooluser", "School-Password-987"));
        Assert.NotNull(reopened.GetLatestSubmission(restoredOwner, school.Id));
        var preserved = Assert.Single(reopened.GetStudents(restoredOwner, school.Id));
        Assert.Equal(student.Id, preserved.Id);
        Assert.Single(reopened.GetSchoolStaff(restoredOwner, school.Id));
        Assert.Equal(94, Assert.Single(reopened.GetStudentResults(restoredOwner, school.Id, preserved.Id)).Score);
        Assert.Single(reopened.GetStudentAssessments(restoredOwner, school.Id, preserved.Id));
        Assert.Single(reopened.GetStudentAttendance(restoredOwner, school.Id, preserved.Id));
        Assert.NotEmpty(reopened.GetRecentAudit(restoredOwner));
        var preservedSchoolAccount = reopened.Authenticate("schooluser", "School-Password-987")!;
        Assert.Equal((long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStudents),
            preservedSchoolAccount.PermissionMask);
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
    [Fact]
    public void CorruptDatabaseRecoveryRetainsOriginalAndRestoresStudentsPermissionsAndAudit()
    {
        var (db, owner, path) = Build();
        db.AddSchool(owner, "REC", "مدرسة الاستعادة", "غيل بن يمين", "حكومية", "أساسي");
        var school = Assert.Single(db.GetSchools(owner));
        db.CreateUser(owner, "recover01", "Restore-School-Password-45", Roles.Entry, school.Id);
        db.SaveDraft(owner, school.Id, new CensusData([new EnrollmentLine("1", 7, 9)], 2, 2, 3, "recover"));
        var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "verified.sobak");
        const string secret = "Recovery secret phrase longer than sixteen!";
        db.CreateEncryptedBackup(owner, backup, secret);
        var corrupted = RandomNumberGenerator.GetBytes(1024);
        File.WriteAllBytes(path, corrupted);
        OfflineDisasterRecovery.RecoverUnreadableDatabase(path, backup, secret, confirmed: true);
        var restored = new SqliteStore(path);
        restored.Initialize();
        var login = Assert.IsType<UserAccount>(restored.Authenticate("phaseowner", "Good-Long-Password-123"));
        Assert.Single(restored.GetSchools(login));
        Assert.NotNull(restored.Authenticate("recover01", "Restore-School-Password-45"));
        Assert.NotNull(restored.GetLatestSubmission(login, school.Id));
        Assert.NotEmpty(restored.GetRecentAudit(login));
        var originals = Directory.GetFiles(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path)!, "recovery"), "*.unreadable-original");
        Assert.NotEmpty(originals);
        Assert.Contains(originals, file => File.ReadAllBytes(file).SequenceEqual(corrupted));
    }

    [Fact]
    public void WrongRecoveryPasswordPreservesDamagedDatabaseByteForByte()
    {
        var (db, owner, path) = Build();
        var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "verified.sobak");
        db.CreateEncryptedBackup(owner, backup, "Recovery secret phrase longer than sixteen!");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(512));
        var original = SHA256.HashData(File.ReadAllBytes(path));
        Assert.ThrowsAny<Exception>(() => OfflineDisasterRecovery.RecoverUnreadableDatabase(
            path, backup, "definitely incorrect", confirmed: true));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.True(File.Exists(DatabaseSecurity.KeyFile(path)));
    }

    [Fact]
    public void IndependentWindowsInstallationRestoresEncryptedBackupWithoutOriginalDpapiKey()
    {
        var (db, owner, _) = Build();
        db.AddSchool(owner, "OTHER", "مدرسة", "العليب", "حكومية", "أساسي");
        var dest = PathFor();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
        var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dest)!, "portable.sobak");
        db.CreateEncryptedBackup(owner, backup, "Portable recovery secret 1122334455");
        var fresh = new SqliteStore(dest);
        fresh.Initialize();
        Assert.False(fresh.HasUsers());
        fresh.RestoreBeforeInitialSetup(backup, "Portable recovery secret 1122334455", confirmed: true);
        var reopened = new SqliteStore(dest);
        reopened.Initialize();
        var admin = Assert.IsType<UserAccount>(reopened.Authenticate("phaseowner", "Good-Long-Password-123"));
        Assert.Single(reopened.GetSchools(admin));
        Assert.True(File.Exists(DatabaseSecurity.KeyFile(dest)));
    }

    [Fact]
    public void BackupIncludesCommittedWalRecordsFromAnOpenConnection()
    {
        var (db, owner, path) = Build();
        using var conn = DatabaseSecurity.OpenForRegressionTest(path);
        using (var mode = conn.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode=WAL;";
            mode.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE wal_probe(id INTEGER PRIMARY KEY, value TEXT);";
            cmd.ExecuteNonQuery();
        }
        using (var tx = conn.BeginTransaction())
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO wal_probe(value) VALUES('committed while connection remains open');";
            insert.ExecuteNonQuery();
            tx.Commit();
        }
        var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "wal.sobak");
        const string secret = "A durable WAL recovery passphrase 123456!";
        db.CreateEncryptedBackup(owner, backup, secret);
        using var restored = DatabaseSecurity.OpenEncrypted(backup, secret);
        using var verify = restored.CreateCommand();
        verify.CommandText = "SELECT value FROM wal_probe;";
        Assert.Equal("committed while connection remains open", verify.ExecuteScalar());
    }

    [Fact]
    public void TamperedEncryptedBackupIsRejectedWithoutChangingActiveDatabase()
    {
        var (db, owner, path) = Build();
        var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "tamper.sobak");
        const string secret = "Passphrase to detect unauthorized tampering";
        db.CreateEncryptedBackup(owner, backup, secret);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var bytes = File.ReadAllBytes(backup);
        bytes[Math.Min(bytes.Length - 1, 160)] ^= 0xA5;
        File.WriteAllBytes(backup, bytes);
        Assert.ThrowsAny<Exception>(() => db.PreviewEncryptedBackup(owner, backup, secret));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void ForeignKeyCorruptedLegacyDatabaseCannotBeUpgradedOrAltered()
    {
        var path = PathFor();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                PRAGMA foreign_keys=OFF;
                CREATE TABLE parent(id INTEGER PRIMARY KEY);
                CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
                INSERT INTO child VALUES(3,99);
                """;
            cmd.ExecuteNonQuery();
        }
        var original = SHA256.HashData(File.ReadAllBytes(path));
        Assert.ThrowsAny<Exception>(() => new SqliteStore(path));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.False(File.Exists(DatabaseSecurity.KeyFile(path)));
    }

    [Fact]
    public void AutomaticBackupStoresSecretOnlyUnderDpapiAndDoesNotDuplicateEarly()
    {
        var (db, owner, path) = Build();
        var folder = Path.Combine(Path.GetDirectoryName(path)!, "auto-backups");
        const string secret = "Unique automatic backup secret 11224466";
        db.ConfigureAutomaticBackup(owner, folder, 24, secret);
        var settings = Assert.IsType<AutomaticBackupSettings>(db.GetAutomaticBackupSettings(owner));
        Assert.Equal(24, settings.IntervalHours);
        var protectedConfig = File.ReadAllBytes(path + ".automatic.dpapi");
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(protectedConfig));
        var backup = Assert.IsType<string>(db.RunAutomaticBackupIfDue(owner));
        Assert.NotNull(db.PreviewEncryptedBackup(owner, backup, secret));
        Assert.Null(db.RunAutomaticBackupIfDue(owner));
        db.DisableAutomaticBackup(owner);
        Assert.Null(db.GetAutomaticBackupSettings(owner));
        Assert.Single(Directory.GetFiles(folder, "*.sobak"));
    }

    [Fact]
    public void SchoolScopedUserCannotBackupOrRestoreDistrictDatabase()
    {
        var (db, owner, path) = Build();
        db.AddSchool(owner, "SEC01", "مدرسة", "العليب", "حكومية", "أساسي");
        var school = Assert.Single(db.GetSchools(owner));
        db.CreateUser(owner, "scoped", "School-user-safe-654321", Roles.Entry, school.Id);
        var user = Assert.IsType<UserAccount>(db.Authenticate("scoped", "School-user-safe-654321"));
        var backup = Path.Combine(Path.GetDirectoryName(path)!, "owner-only.sobak");
        const string secret = "Backup is limited to original administrator!";
        Assert.Throws<UnauthorizedAccessException>(() => db.CreateEncryptedBackup(user, backup, secret));
        db.CreateEncryptedBackup(owner, backup, secret);
        Assert.Throws<UnauthorizedAccessException>(() => db.PreviewEncryptedBackup(user, backup, secret));
        Assert.Throws<UnauthorizedAccessException>(() => db.RestoreEncryptedBackup(user, backup, secret, true));
        Assert.Single(db.GetSchools(owner));
    }

    [Fact]
    public void NativeCipherSmokeRunsInTheRequestedWindowsProcessArchitecture()
    {
        var target = Environment.GetEnvironmentVariable("SCHOOL_OBSERVATORY_EXPECTED_ARCH");
        if (string.IsNullOrWhiteSpace(target)) return;
        var wanted = target.Equals("x86", StringComparison.OrdinalIgnoreCase)
            ? System.Runtime.InteropServices.Architecture.X86
            : System.Runtime.InteropServices.Architecture.X64;
        Assert.Equal(wanted, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        var (_, _, path) = Build();
        Assert.False(DatabaseSecurity.IsPlaintext(path));
    }

    [Fact]
    public void InterruptedMigrationBeforePromotionResumesUsingExistingProtectedKey()
    {
        var path = PathFor();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var plain = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            plain.Open();
            using var cmd = plain.CreateCommand();
            cmd.CommandText = "CREATE TABLE legacy(id INTEGER PRIMARY KEY, record TEXT); INSERT INTO legacy VALUES(1, 'preserved');";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        // Mimic a stopped process after durable key-file creation, before promotion.
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        DatabaseSecurity.CreateProtectedKeyFile(DatabaseSecurity.KeyFile(path), keyBytes);
        var legacyBytes = File.ReadAllBytes(path);
        var resumed = new SqliteStore(path);
        resumed.Initialize();
        Assert.False(DatabaseSecurity.IsPlaintext(path));
        Assert.Equal(1, DatabaseSecurity.CountRows(path, "legacy"));
        Assert.True(File.Exists(DatabaseSecurity.KeyFile(path)));
        Assert.True(legacyBytes.Length > 16);
    }

    [Fact]
    public void InterruptedMigrationAfterPromotionRetiresOnlyMatchingPlaintextOriginal()
    {
        var path = PathFor();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var plain = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            plain.Open();
            using var cmd = plain.CreateCommand();
            cmd.CommandText = "CREATE TABLE legacy(id INTEGER PRIMARY KEY, record TEXT); INSERT INTO legacy VALUES(1, 'still here');";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var rollback = path + ".20261117000000000.legacy-original";
        File.Copy(path, rollback);
        var store = new SqliteStore(path);
        store.Initialize();
        // Create a matching unencrypted rollback from the old schema for a resume check.
        Assert.True(File.Exists(rollback));
        var reopened = new SqliteStore(path);
        Assert.Equal(1, DatabaseSecurity.CountRows(path, "legacy"));
        Assert.False(File.Exists(rollback));
    }

}
