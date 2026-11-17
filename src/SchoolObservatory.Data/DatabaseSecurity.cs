using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SchoolObservatory.Data;

/// <summary>
/// SQLCipher 4 compatible storage (SQLite3MC), user-scoped Windows DPAPI key and
/// fail-closed legacy migration. Never infer that a missing key means an empty database.
/// </summary>
public static class DatabaseSecurity
{
    private const string SQLiteHeader = "SQLite format 3\0";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SchoolObservatory/v1/database-key");
    private static readonly object Gate = new();

    public static string KeyFile(string databasePath) => Path.GetFullPath(databasePath) + ".key.dpapi";

    private static string FullPath(string path) => Path.GetFullPath(path);

    private static void WindowsOnly()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected local educational records require Windows DPAPI.");
    }

    private static void SecureDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(directory);
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Windows account SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var who in new[]
        {
            sid,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void SecureFile(string file)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!File.Exists(file)) return;
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Windows account SID is unavailable.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var who in new[]
        {
            sid,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl,
                AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }

    public static bool IsPlaintext(string databasePath)
    {
        using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < 16) throw new InvalidDataException("Invalid or truncated SQLite database.");
        var header = new byte[16];
        stream.ReadExactly(header);
        return header.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(SQLiteHeader));
    }

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private static byte[] ReadKey(string keyFile)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var blob = File.ReadAllBytes(keyFile);
        if (blob.Length is < 40 or > 4096) throw new CryptographicException("Invalid DPAPI database key file.");
        var raw = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        if (raw.Length != 32) throw new CryptographicException("Invalid database encryption key.");
        return raw;
    }

    private static void WriteKey(string keyFile, byte[] key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var wrapped = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
        using (var stream = new FileStream(keyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(wrapped);
            stream.Flush(true);
        }
        SecureFile(keyFile);
    }

    private static string ReadHexKey(string keyFile)
    {
        var key = ReadKey(keyFile);
        try { return Convert.ToHexString(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Open a SQLCipher 4 file using a password or random hex key. Never use ReadWriteCreate for existing records.</summary>
    public static SqliteConnection OpenEncrypted(string path, string passphrase, bool create = false, bool readOnly = false)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("Encryption key is missing.");
        var uri = new Uri(FullPath(path)).AbsoluteUri + "?cipher=sqlcipher&legacy=4";
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Password = passphrase,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        };
        var con = new SqliteConnection(builder.ToString());
        try
        {
            con.Open();
            using var verify = con.CreateCommand();
            verify.CommandText = "SELECT count(*) FROM sqlite_master;";
            _ = verify.ExecuteScalar(); // Wrong password must fail before any schema write.
            return con;
        }
        catch
        {
            con.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenPlaintext(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = FullPath(path), Mode = SqliteOpenMode.ReadWrite,
            Pooling = false, Cache = SqliteCacheMode.Private, ForeignKeys = true
        };
        var con = new SqliteConnection(builder.ToString());
        con.Open();
        if (!IsPlaintext(path))
        {
            con.Dispose();
            throw new InvalidDataException("Source is not a plaintext SQLite database.");
        }
        return con;
    }

    private static void VerifyIntegrity(SqliteConnection con)
    {
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(cmd.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite integrity check failed.");
        }
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_key_check;";
            using var r = cmd.ExecuteReader();
            if (r.Read()) throw new InvalidDataException("SQLite foreign-key integrity failed.");
        }
    }

    public static IReadOnlyDictionary<string, long> TableCounts(SqliteConnection con)
    {
        VerifyIntegrity(con);
        var names = new List<string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
        }
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            counts.Add(name, (long)(cmd.ExecuteScalar() ?? 0L));
        }
        return counts;
    }

    /// <summary>SHA-256 of every typed value in every application table, in deterministic order.</summary>
    private static IReadOnlyDictionary<string, string> TableFingerprints(SqliteConnection con)
    {
        var names = TableCounts(con).Keys;
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var escaped = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            using var countColumns = con.CreateCommand();
            countColumns.CommandText = "SELECT * FROM " + escaped + " LIMIT 0";
            using var schema = countColumns.ExecuteReader();
            var order = string.Join(",", Enumerable.Range(1, schema.FieldCount));
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM " + escaped + (order.Length > 0 ? " ORDER BY " + order : "");
            using var r = cmd.ExecuteReader();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                {
                    var value = r.GetValue(i);
                    string encoded = value switch
                    {
                        DBNull => "N",
                        byte[] rawBlob => "B" + Convert.ToBase64String(rawBlob),
                        string s => "S" + s,
                        long n => "I" + n.ToString(CultureInfo.InvariantCulture),
                        int n => "I" + n.ToString(CultureInfo.InvariantCulture),
                        double n => "D" + n.ToString("R", CultureInfo.InvariantCulture),
                        _ => "O" + Convert.ToString(value, CultureInfo.InvariantCulture)
                    };
                    var bytes = Encoding.UTF8.GetBytes(encoded);
                    hash.AppendData(BitConverter.GetBytes(bytes.Length));
                    hash.AppendData(bytes);
                }
            result.Add(name, Convert.ToHexString(hash.GetHashAndReset()));
        }
        return result;
    }

    public static void CompareAllRecords(SqliteConnection source, SqliteConnection destination)
    {
        var first = TableFingerprints(source);
        var second = TableFingerprints(destination);
        if (first.Count != second.Count || first.Any(pair =>
                !second.TryGetValue(pair.Key, out var fingerprint) || fingerprint != pair.Value))
            throw new InvalidDataException("Encrypted database differs from the original records.");
    }

    public static string Prepare(string databasePath)
    {
        WindowsOnly();
        var db = FullPath(databasePath);
        var keyFile = KeyFile(db);
        lock (Gate)
        {
            SecureDirectory(Path.GetDirectoryName(db)!);
            if (File.Exists(keyFile))
            {
                var key = ReadHexKey(keyFile);
                if (!File.Exists(db))
                    throw new InvalidDataException("Database missing; will not create an empty replacement. Restore from a verified backup.");
                if (IsPlaintext(db))
                {
                    // A previous migration may have stopped before the atomic replacement.
                    MigrateLegacy(db, key, keyFile, false);
                }
                using var con = OpenEncrypted(db, key);
                VerifyIntegrity(con);
                SecureFile(db);
                SecureFile(keyFile);
                return key;
            }

            if (File.Exists(db))
            {
                if (!IsPlaintext(db))
                    throw new CryptographicException("Encrypted database exists but its Windows key is missing. Restore using an authorized backup.");
                var raw = NewKey();
                try
                {
                    var key = Convert.ToHexString(raw);
                    WriteKey(keyFile, raw);
                    MigrateLegacy(db, key, keyFile, true);
                    return key;
                }
                finally { CryptographicOperations.ZeroMemory(raw); }
            }

            var fresh = NewKey();
            try
            {
                var key = Convert.ToHexString(fresh);
                WriteKey(keyFile, fresh);
                using var con = OpenEncrypted(db, key, create: true);
                // Force an actual SQLCipher database page before checking its on-disk header.
                // SELECT sqlite_master alone may leave a zero-byte uninitialized file.
                using (var marker = con.CreateCommand())
                {
                    marker.CommandText = "PRAGMA application_id=1397707276;";
                    marker.ExecuteNonQuery();
                }
                VerifyIntegrity(con);
                if (IsPlaintext(db))
                    throw new CryptographicException("Native SQLite engine is not encrypting data.");
                SecureFile(db);
                return key;
            }
            finally { CryptographicOperations.ZeroMemory(fresh); }
        }
    }

    private static void MigrateLegacy(string db, string key, string keyFile, bool keyCreated)
    {
        string candidate = db + "." + Guid.NewGuid().ToString("N") + ".encrypted-candidate";
        string original = db + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".legacy-original";
        bool replaced = false;
        try
        {
            using (var source = OpenPlaintext(db))
            {
                VerifyIntegrity(source);
                using (var target = OpenEncrypted(candidate, key, create: true))
                {
                    source.BackupDatabase(target); // Includes committed WAL pages atomically.
                    CompareAllRecords(source, target);
                    if (IsPlaintext(candidate))
                        throw new CryptographicException("SQLCipher migration produced plaintext.");
                }
                // Flush legacy WAL through SQLite itself; never copy/delete a live -wal file.
                using var checkpoint = source.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
                checkpoint.ExecuteNonQuery();
            }

            SecureFile(candidate);
            if (File.Exists(db + "-wal") || File.Exists(db + "-shm"))
                throw new IOException("Active legacy WAL sidecars remain; close all other instances before migrating.");

            File.Replace(candidate, db, original); // Atomic same-volume promotion.
            replaced = true;
            using (var verified = OpenEncrypted(db, key))
            {
                VerifyIntegrity(verified);
                if (IsPlaintext(db))
                    throw new CryptographicException("Database not encrypted after migration.");
            }
            // Data has been verified. Keep an encrypted recovery snapshot, not a plaintext original.
            File.Delete(original);
            SecureFile(db);
        }
        catch
        {
            if (replaced && File.Exists(original))
            {
                try { File.Replace(original, db, null); replaced = false; }
                catch { /* Leave original available and never silently overwrite it. */ }
            }
            if (!replaced && keyCreated && File.Exists(keyFile))
                File.Delete(keyFile);
            throw;
        }
        finally
        {
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    public static void VerifyWithKey(string databasePath, string wrongOrActualKey)
    {
        using var con = OpenEncrypted(databasePath, wrongOrActualKey);
        VerifyIntegrity(con);
    }

    public static long CountRows(string databasePath, string table)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(table, "^[A-Za-z_][A-Za-z_0-9]*$"))
            throw new ArgumentException("Invalid table name.");
        var key = ReadHexKey(KeyFile(databasePath));
        using var con = OpenEncrypted(databasePath, key);
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM \"" + table + "\"";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    internal static void ProtectLocalFile(string path) => SecureFile(path);

    internal static string ReadCurrentKey(string databasePath) => ReadHexKey(KeyFile(databasePath));

    internal static string BackupSchemaBeforeChanges(string databasePath, string key)
    {
        var root = Path.Combine(Path.GetDirectoryName(FullPath(databasePath))!, "recovery");
        SecureDirectory(root);
        var output = Path.Combine(root, "before-schema-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".db");
        using var source = OpenEncrypted(databasePath, key);
        using var dest = OpenEncrypted(output, key, create: true);
        source.BackupDatabase(dest);
        CompareAllRecords(source, dest);
        if (IsPlaintext(output)) throw new CryptographicException("Schema backup was not encrypted.");
        SecureFile(output);
        return output;
    }
}
