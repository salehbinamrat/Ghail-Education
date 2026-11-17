using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed record EncryptedBackupPreview(
    string FileName, long SizeBytes, IReadOnlyDictionary<string, long> RowCounts);

public sealed record AutomaticBackupSettings(string Directory, int IntervalHours);

public sealed partial class SqliteStore
{
    private static readonly object BackupGate = new();
    private static readonly byte[] AutoEntropy = Encoding.UTF8.GetBytes("SchoolObservatory/v1/automatic-backup");
    private sealed record AutomaticConfig(string Directory, int IntervalHours, string Passphrase);
    private string AutoConfigFile => _databasePath + ".automatic.dpapi";
    private string AutoLastRunFile => _databasePath + ".automatic-last-run";

    private void RequireBackupOwner(UserAccount actor)
    {
        RequireFreshActor(actor);
        if (!actor.IsOwner)
            throw new UnauthorizedAccessException("Only the original administrator can export or replace the complete database.");
    }

    private static void AssertBackupSecret(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 16 || passphrase.Length > 256)
            throw new ArgumentException("Backup recovery password must be 16 to 256 characters.");
    }

    private static void AssertNotLivePath(string path, string live)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(live), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(path), DatabaseSecurity.KeyFile(live), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A backup cannot overwrite the active database or key.");
    }

    private static void Execute(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AssertEducationBackup(SqliteConnection con)
    {
        var counts = DatabaseSecurity.TableCounts(con);
        foreach (var table in new[] { "schools", "users", "submissions", "audit_events",
            "school_staff", "school_students", "student_results" })
            if (!counts.ContainsKey(table))
                throw new InvalidDataException("The selected file is not a complete School Observatory backup.");
    }

    /// <summary>
    /// Online SQLite backup API captures the committed WAL snapshot; SQLCipher 4 encrypts
    /// database pages and authenticates them. No plaintext temp file is ever created.
    /// </summary>
    public void CreateEncryptedBackup(UserAccount actor, string destination, string passphrase)
    {
        RequireBackupOwner(actor);
        CreateBackupInternal(destination, passphrase);
    }

    private void CreateBackupInternal(string destination, string passphrase)
    {
        AssertBackupSecret(passphrase);
        var full = Path.GetFullPath(destination);
        AssertNotLivePath(full, _databasePath);
        if (File.Exists(full)) throw new IOException("Backup destination exists. Choose a new filename.");
        var folder = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Backup directory does not exist.");
        var candidate = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        lock (BackupGate)
        {
            try
            {
                using (var source = Open())
                using (var target = DatabaseSecurity.OpenEncrypted(candidate, passphrase, create: true))
                {
                    source.BackupDatabase(target);
                    DatabaseSecurity.CompareAllRecords(source, target);
                    AssertEducationBackup(target);
                    if (DatabaseSecurity.IsPlaintext(candidate))
                        throw new CryptographicException("The backup was not encrypted.");
                }
                File.Move(candidate, full); // Never overwrite an existing recovery point.
            }
            finally { if (File.Exists(candidate)) File.Delete(candidate); }
        }
    }

    public EncryptedBackupPreview PreviewEncryptedBackup(UserAccount actor, string backupPath, string passphrase)
    {
        RequireBackupOwner(actor);
        return PreviewBackupInternal(backupPath, passphrase);
    }

    private static EncryptedBackupPreview PreviewBackupInternal(string backupPath, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("Recovery password is required.");
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup not found.", backupPath);
        if (DatabaseSecurity.IsPlaintext(backupPath))
            throw new InvalidDataException("An unencrypted SQLite file is not an authorized encrypted backup.");
        using var backup = DatabaseSecurity.OpenEncrypted(backupPath, passphrase, readOnly: true);
        AssertEducationBackup(backup);
        return new EncryptedBackupPreview(Path.GetFileName(backupPath),
            new FileInfo(backupPath).Length, DatabaseSecurity.TableCounts(backup));
    }

    public void RestoreEncryptedBackup(UserAccount actor, string backupPath, string passphrase, bool confirmed)
    {
        RequireBackupOwner(actor);
        if (!confirmed) throw new InvalidOperationException("Explicit restore confirmation is required.");
        RestoreBackupInternal(backupPath, passphrase);
    }

    /// <summary>Permitted only in first-run setup of a genuinely empty database.</summary>
    public void RestoreBeforeInitialSetup(string backupPath, string passphrase, bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("Explicit restore confirmation is required.");
        if (HasUsers()) throw new UnauthorizedAccessException("An administrator must authorize restore.");
        using (var current = Open())
        {
            var counts = DatabaseSecurity.TableCounts(current);
            if (counts.Any(pair => pair.Value > 0))
                throw new UnauthorizedAccessException("First-run recovery is only allowed on an empty database.");
        }
        RestoreBackupInternal(backupPath, passphrase);
    }

    private void RestoreBackupInternal(string backupPath, string passphrase)
    {
        AssertNotLivePath(backupPath, _databasePath);
        _ = PreviewBackupInternal(backupPath, passphrase);
        lock (BackupGate)
        {
            var candidate = _databasePath + "." + Guid.NewGuid().ToString("N") + ".restore-candidate";
            var rollback = _databasePath + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".pre-restore.db";
            var replaced = false;
            try
            {
                using (var source = DatabaseSecurity.OpenEncrypted(backupPath, passphrase, readOnly: true))
                using (var target = DatabaseSecurity.OpenEncrypted(candidate, _databaseKey, create: true))
                {
                    AssertEducationBackup(source);
                    source.BackupDatabase(target);
                    DatabaseSecurity.CompareAllRecords(source, target);
                    if (DatabaseSecurity.IsPlaintext(candidate))
                        throw new CryptographicException("Restored candidate must be encrypted.");
                }
                // Do not replace files behind a live WAL; flush and change journal mode first.
                using (var live = Open())
                {
                    Execute(live, "PRAGMA wal_checkpoint(TRUNCATE);");
                    Execute(live, "PRAGMA journal_mode=DELETE;");
                }
                if (File.Exists(_databasePath + "-wal") || File.Exists(_databasePath + "-shm"))
                    throw new IOException("WAL is still in use; restore cancelled without changing the database.");

                File.Replace(candidate, _databasePath, rollback);
                replaced = true;
                using (var final = Open())
                using (var original = DatabaseSecurity.OpenEncrypted(backupPath, passphrase, readOnly: true))
                    DatabaseSecurity.CompareAllRecords(original, final);
                // Preserve the previous encrypted database so a mischosen backup is reversible.
                DatabaseSecurity.ProtectLocalFile(rollback);
                DatabaseSecurity.ProtectLocalFile(_databasePath);
            }
            catch
            {
                if (replaced && File.Exists(rollback))
                {
                    try { File.Replace(rollback, _databasePath, null); }
                    catch { /* A verified encrypted rollback file remains available. */ }
                }
                throw;
            }
            finally { if (File.Exists(candidate)) File.Delete(candidate); }
        }
    }

    public AutomaticBackupSettings? GetAutomaticBackupSettings(UserAccount actor)
    {
        RequireBackupOwner(actor);
        var c = ReadAutomaticConfig();
        return c is null ? null : new AutomaticBackupSettings(c.Directory, c.IntervalHours);
    }

    private AutomaticConfig? ReadAutomaticConfig()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!File.Exists(AutoConfigFile)) return null;
        byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(AutoConfigFile), AutoEntropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<AutomaticConfig>(bytes)
                ?? throw new InvalidDataException("Invalid automatic backup configuration.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void ConfigureAutomaticBackup(UserAccount actor, string directory, int intervalHours, string passphrase)
    {
        RequireBackupOwner(actor);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        AssertBackupSecret(passphrase);
        if (intervalHours is < 1 or > 168) throw new ArgumentOutOfRangeException(nameof(intervalHours));
        var full = Path.GetFullPath(directory);
        Directory.CreateDirectory(full);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new AutomaticConfig(full, intervalHours, passphrase));
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, AutoEntropy, DataProtectionScope.CurrentUser);
            var candidate = AutoConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(protectedBytes);
                    stream.Flush(true);
                }
                DatabaseSecurity.ProtectLocalFile(candidate);
                File.Move(candidate, AutoConfigFile, true);
                DatabaseSecurity.ProtectLocalFile(AutoConfigFile);
            }
            finally { if (File.Exists(candidate)) File.Delete(candidate); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void DisableAutomaticBackup(UserAccount actor)
    {
        RequireBackupOwner(actor);
        if (File.Exists(AutoConfigFile)) File.Delete(AutoConfigFile);
    }

    /// <summary>Call periodically while the application is running. Never executes without a configured owner.</summary>
    public string? RunAutomaticBackupIfDue(UserAccount actor)
    {
        RequireBackupOwner(actor);
        lock (BackupGate)
        {
            var config = ReadAutomaticConfig();
            if (config is null) return null;
            if (File.Exists(AutoLastRunFile) &&
                DateTimeOffset.TryParse(File.ReadAllText(AutoLastRunFile), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var last) &&
                DateTimeOffset.UtcNow - last < TimeSpan.FromHours(config.IntervalHours))
                return null;
            Directory.CreateDirectory(config.Directory);
            var backup = Path.Combine(config.Directory,
                "SchoolObservatory-Auto-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..6] + ".sobak");
            CreateBackupInternal(backup, config.Passphrase);
            _ = PreviewBackupInternal(backup, config.Passphrase);
            File.WriteAllText(AutoLastRunFile, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            DatabaseSecurity.ProtectLocalFile(AutoLastRunFile);
            return backup;
        }
    }
}
