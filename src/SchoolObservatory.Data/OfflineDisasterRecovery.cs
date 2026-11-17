using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace SchoolObservatory.Data;

public static partial class OfflineDisasterRecovery
{
    /// <summary>
    /// Last-resort authenticated restore for a database that cannot start. An existing
    /// healthy database may ONLY be replaced through owner-authorized in-app restore.
    /// The old data, DPAPI key and WAL sidecars are preserved in an ACL-protected folder.
    /// </summary>
    public static void RecoverUnreadableDatabase(string databasePath, string backupPath, string passphrase, bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("Explicit recovery confirmation is required.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("Backup passphrase is required.");
        var db = Path.GetFullPath(databasePath);
        var keyFile = DatabaseSecurity.KeyFile(db);
        var backup = Path.GetFullPath(backupPath);
        if (!File.Exists(db)) throw new FileNotFoundException("This operation requires an unreadable existing database.", db);
        if (!File.Exists(backup)) throw new FileNotFoundException("Encrypted backup missing.", backup);
        if (string.Equals(db, backup, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The backup cannot be the live database.");

        // Never provide a way to bypass normal administrator authentication for a healthy database.
        if (File.Exists(keyFile))
        {
            try
            {
                using var existing = DatabaseSecurity.OpenForRecoveryCheck(db);
                // A missing/older application table is not database corruption and must
                // NEVER grant an unauthenticated path around the owner-only restore UI.
                // Integrity and foreign-key verification are sufficient to distinguish
                // a readable database from a genuinely damaged/unopenable one.
                _ = DatabaseSecurity.TableCounts(existing);
                throw new UnauthorizedAccessException("The current database is readable; sign in as its owner to restore.");
            }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or SqliteException or IOException)
            {
                // True data corruption or unreadable DPAPI. User authorization still
                // requires the independent portable backup passphrase.
            }
        }
        else if (DatabaseSecurity.IsPlaintext(db))
        {
            throw new UnauthorizedAccessException("Unencrypted legacy database requires normal migration, not disaster recovery.");
        }

        var destDirectory = Path.GetDirectoryName(db)!;
        var recoveryDirectory = Path.Combine(destDirectory, "recovery");
        DatabaseSecurity.ProtectLocalDirectory(recoveryDirectory);

        var tempDb = db + "." + Guid.NewGuid().ToString("N") + ".recovery-candidate";
        var tempKey = keyFile + "." + Guid.NewGuid().ToString("N") + ".temporary";
        var newKey = RandomNumberGenerator.GetBytes(32);
        var moved = new List<(string From, string To)>();
        var installedDb = false;
        var installedKey = false;
        try
        {
            using (var original = DatabaseSecurity.OpenEncrypted(backup, passphrase, readOnly: true))
            {
                DatabaseSecurity.ValidateRecoverySource(original);
                DatabaseSecurity.VacuumToEncrypted(original, tempDb, Convert.ToHexString(newKey));
                using var restored = DatabaseSecurity.OpenEncrypted(tempDb, Convert.ToHexString(newKey));
                DatabaseSecurity.ValidateRecoverySource(restored);
                DatabaseSecurity.CompareAllRecords(original, restored);
                if (DatabaseSecurity.IsPlaintext(tempDb))
                    throw new CryptographicException("Restoration candidate is not encrypted.");
            }

            DatabaseSecurity.CreateProtectedKeyFile(tempKey, newKey);
            DatabaseSecurity.ProtectLocalFile(tempDb);

            var tag = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) +
                      "-" + Guid.NewGuid().ToString("N");
            foreach (var source in new[] { db, db + "-wal", db + "-shm", keyFile })
            {
                if (!File.Exists(source)) continue;
                var destination = Path.Combine(recoveryDirectory,
                    Path.GetFileName(source) + "." + tag + ".unreadable-original");
                File.Move(source, destination);
                moved.Add((source, destination));
                DatabaseSecurity.ProtectLocalFile(destination);
            }

            File.Move(tempDb, db);
            installedDb = true;
            File.Move(tempKey, keyFile);
            installedKey = true;
            using (var reopened = DatabaseSecurity.OpenForRecoveryCheck(db))
                DatabaseSecurity.ValidateRecoverySource(reopened);
            DatabaseSecurity.ProtectLocalFile(db);
            DatabaseSecurity.ProtectLocalFile(keyFile);
        }
        catch
        {
            // Do not leave an empty or partly installed database visible as successful recovery.
            if (installedDb && File.Exists(db)) File.Delete(db);
            if (installedKey && File.Exists(keyFile)) File.Delete(keyFile);
            foreach (var pair in moved.AsEnumerable().Reverse())
                if (File.Exists(pair.To) && !File.Exists(pair.From))
                    File.Move(pair.To, pair.From);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newKey);
            if (File.Exists(tempDb)) File.Delete(tempDb);
            if (File.Exists(tempKey)) File.Delete(tempKey);
        }
    }
}
