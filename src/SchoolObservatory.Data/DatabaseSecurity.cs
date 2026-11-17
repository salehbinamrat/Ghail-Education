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
        var counts = TableCounts(con);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in counts.Keys)
        {
            var escaped = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            using var countColumns = con.CreateCommand();
            countColumns.CommandText = "SELECT * FROM " + escaped + " LIMIT 0";
            using var schema = countColumns.ExecuteReader();
            var columnCount = schema.FieldCount;
            var order = string.Join(",", Enumerable.Range(1, columnCount));
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM " + escaped + (order.Length > 0 ? " ORDER BY " + order : "");
            using var r = cmd.ExecuteReader();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Include record and column boundaries, not just concatenated cell values.
            // Otherwise two one-column rows could hash like one two-column row.
            hash.AppendData(BitConverter.GetBytes(counts[name]));
            hash.AppendData(BitConverter.GetBytes(columnCount));
            while (r.Read())
            {
                hash.AppendData(new byte[] { 0xFE }); // Unambiguous row boundary.
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
            }
            result.Add(name, Convert.ToHexString(hash.GetHashAndReset()));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> SchemaDefinitions(SqliteConnection con)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT type,name,tbl_name,COALESCE(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name,tbl_name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(string.Join("/", reader.GetString(0), reader.GetString(1), reader.GetString(2)), reader.GetString(3));
        return result;
    }

    public static void CompareAllRecords(SqliteConnection source, SqliteConnection destination)
    {
        var first = TableFingerprints(source);
        var second = TableFingerprints(destination);
        if (first.Count != second.Count || first.Any(pair =>
                !second.TryGetValue(pair.Key, out var fingerprint) || fingerprint != pair.Value))
            throw new InvalidDataException("Encrypted database differs from the original records.");
        // Check definitions of all application tables, indexes, triggers and views:
        // equal cell values alone do not prove that all constraints were preserved.
        var sourceSchema = SchemaDefinitions(source);
        var destinationSchema = SchemaDefinitions(destination);
        if (sourceSchema.Count != destinationSchema.Count || sourceSchema.Any(pair =>
                !destinationSchema.TryGetValue(pair.Key, out var definition) || definition != pair.Value))
            throw new InvalidDataException("Encrypted database schema differs from the original.");
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
                RetireVerifiedInterruptedRollbacks(db, con);
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

    /// <summary>
    /// If a process stopped immediately after atomic promotion but before deleting
    /// the original plaintext, verify exact content before retiring that original.
    /// Never silently discard a rollback with different records.
    /// </summary>
    private static void RetireVerifiedInterruptedRollbacks(string db, SqliteConnection encrypted)
    {
        var folder = Path.GetDirectoryName(db)!;
        var name = Path.GetFileName(db);
        var rollbacks = Directory.GetFiles(folder, name + ".*.legacy-original");
        foreach (var rollback in rollbacks)
        {
            using (var legacy = OpenPlaintext(rollback))
                CompareAllRecords(legacy, encrypted);
            // CompareAllRecords must finish and the connection must close first.
            File.Delete(rollback);
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
                // Refuse an unknown future schema before writing an encryption candidate
                // or retiring the legacy plaintext file. Its current owner must use a
                // compatible newer application, not silently downgrade its metadata.
                using (var version = source.CreateCommand())
                {
                    version.CommandText = "PRAGMA user_version;";
                    if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) > 2)
                        throw new InvalidDataException("Cannot migrate a database created by a newer unsupported application version.");
                }
                // SQLite3MC cannot use sqlite3_backup across plaintext/encrypted databases.
                // VACUUM INTO produces a consistent online snapshot INCLUDING committed WAL.
                // SQLCipher is activated directly for the target URI: no plaintext staging copy.
                var targetUri = new Uri(candidate).AbsoluteUri +
                    "?cipher=sqlcipher&legacy=4&key=" + Uri.EscapeDataString(key);
                using (var vacuum = source.CreateCommand())
                {
                    vacuum.CommandText = "VACUUM INTO $target;";
                    vacuum.Parameters.AddWithValue("$target", targetUri);
                    vacuum.ExecuteNonQuery();
                }
                using (var encrypted = OpenEncrypted(candidate, key))
                {
                    CompareAllRecords(source, encrypted);
                    if (IsPlaintext(candidate))
                        throw new CryptographicException("SQLCipher migration produced plaintext.");

                    var recoveryRoot = Path.Combine(Path.GetDirectoryName(db)!, "recovery");
                    SecureDirectory(recoveryRoot);
                    var encryptedSnapshot = Path.Combine(recoveryRoot,
                        "before-encryption-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) +
                        "-" + Guid.NewGuid().ToString("N") + ".db");
                    using (var saved = OpenEncrypted(encryptedSnapshot, key, create: true))
                    {
                        encrypted.BackupDatabase(saved);
                        CompareAllRecords(encrypted, saved);
                        if (IsPlaintext(encryptedSnapshot))
                            throw new CryptographicException("The recovery snapshot is not encrypted.");
                    }
                    SecureFile(encryptedSnapshot);
                }

                // SQLite handles WAL itself. Fail closed if any other process is using it.
                using (var checkpoint = source.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    checkpoint.ExecuteNonQuery();
                }
                using (var journal = source.CreateCommand())
                {
                    journal.CommandText = "PRAGMA journal_mode=DELETE;";
                    journal.ExecuteNonQuery();
                }
            }

            // Earlier plain SQLite versions, import tools and third-party readers
            // may have used Microsoft.Data.Sqlite's default connection pooling.
            // Disposing the managed connection does not close its pooled native VFS
            // file handle: flush our process's idle pools before Windows File.Replace.
            SqliteConnection.ClearAllPools();
            SecureFile(candidate);
            if (File.Exists(db + "-wal") || File.Exists(db + "-shm"))
                throw new IOException("Active WAL sidecars remain; close other instances before migrating.");

            AtomicReplace(candidate, db, original);
            replaced = true;
            using (var verified = OpenEncrypted(db, key))
            {
                VerifyIntegrity(verified);
                if (IsPlaintext(db))
                    throw new CryptographicException("Database not encrypted after migration.");
            }
            File.Delete(original); // Encrypted recovery snapshot replaces plaintext recovery.
            SecureFile(db);
        }
        catch
        {
            if (replaced && File.Exists(original))
            {
                try { File.Replace(original, db, null); replaced = false; }
                catch { /* Keep the available original; never erase it. */ }
            }
            if (!replaced && keyCreated && File.Exists(keyFile))
                File.Delete(keyFile);
            throw;
        }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
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

    internal static void ProtectLocalDirectory(string path) => SecureDirectory(path);
    internal static void CreateProtectedKeyFile(string path, byte[] key) => WriteKey(path, key);

    internal static SqliteConnection OpenForRecoveryCheck(string db) =>
        OpenEncrypted(db, ReadHexKey(KeyFile(db)));

    internal static void ValidateRecoverySource(SqliteConnection con)
    {
        var counts = TableCounts(con);
        foreach (var required in new[] { "users", "schools", "submissions", "audit_events",
            "school_staff", "school_students", "student_results" })
            if (!counts.ContainsKey(required))
                throw new InvalidDataException("Recovery source is not a complete School Observatory database.");
    }

    /// <summary>Windows may briefly retain an SQLite VFS sharing handle while closing.</summary>
    internal static void AtomicReplace(string candidate, string current, string rollback)
    {
        IOException? last = null;
        for (var retry = 0; retry < 8; retry++)
        {
            try
            {
                File.Replace(candidate, current, rollback);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                if (retry == 7) break;
                System.Threading.Thread.Sleep(75 * (retry + 1));
            }
        }
        throw new IOException("Could not atomically promote the verified SQLite snapshot; existing data remains intact.", last);
    }

    internal static void VacuumToEncrypted(SqliteConnection source, string output, string passphrase)
    {
        if (File.Exists(output)) throw new IOException("Encrypted snapshot already exists.");
        var destinationUri = new Uri(FullPath(output)).AbsoluteUri +
            "?cipher=sqlcipher&legacy=4&key=" + Uri.EscapeDataString(passphrase);
        using var command = source.CreateCommand();
        command.CommandText = "VACUUM INTO $destination;";
        command.Parameters.AddWithValue("$destination", destinationUri);
        command.ExecuteNonQuery();
    }

    internal static void ProtectLocalFile(string path) => SecureFile(path);

    // Internal-only regression hook: never exposed through application UI.
    internal static SqliteConnection OpenForRegressionTest(string databasePath) =>
        OpenEncrypted(databasePath, ReadHexKey(KeyFile(databasePath)));

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
