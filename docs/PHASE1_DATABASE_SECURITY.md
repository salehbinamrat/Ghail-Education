# Phase 1 — Local Database Encryption, Backup, Recovery and Authentication

**Status:** Branch implementation; NOT approved for production until BOTH Windows Core and Windows Release pass every regression test on the same commit and every four Windows package files have been verified. This document covers only Phase 1.

## Design and threat model

- Windows 10/11, .NET 10, local offline single-user-access database. Not intended for concurrent multi-PC access, shared drives, or a network server.
- The data file \`%LOCALAPPDATA%\SchoolObservatory\observatory.db\` is encrypted using the SQLCipher 4-compatible cipher of SQLite3 Multiple Ciphers. The SQLite3MC native bundle must be present for the exact running architecture (win-x86 or win-x64).
- A cryptographically random 32-byte database secret is wrapped with Windows DPAPI CurrentUser and stored at \`observatory.db.key.dpapi\`. Its value is neither hardcoded nor written to logs or the database.
- Windows discretionary ACLs are applied to the application data directory and sensitive local files, limited to the Windows account, Administrators and SYSTEM. BitLocker or device encryption is additionally recommended to mitigate forensic recovery of previously unencrypted blocks or removed files, because deletion is NOT guaranteed to overwrite SSD media.
- SQLite PRAGMA integrity_check and foreign_key_check and table-by-table sorted typed-data SHA-256 fingerprints verify migrations, backups and restores. Hash verification detects accidental mismatch but does not replace the SQLCipher per-page HMAC.
- PBKDF2-SHA256 hashes user passwords (350,000 iterations, 16-byte salt, 32-byte output) and rejects weak new passwords; user account permission and auth_version checking and 5-attempt/5-minute lockout remain enforced. Optional saved login credentials stay in Windows Credential Manager.
- There are no embedded master secrets or default administrator passwords. Losing both the DPAPI-protected key and every portable backup passphrase makes encrypted data unrecoverable. Do NOT depend on a hidden recovery backdoor.

## New installation and legacy migration

1. Program opens the per-user data directory and limits its Windows ACL.
2. If an encrypted data file exists but the matching DPAPI key is missing, startup fails closed; it must **never** create an empty replacement database.
3. If a legacy unencrypted SQLite file exists, the program validates its integrity, creates an encrypted online snapshot using SQLite's \`VACUUM INTO\` and SQLCipher-configured destination URI, compares every table and row fingerprint, and writes a separate verified encrypted recovery point in the \`recovery\` subdirectory.
4. Only after verification does it checkpoint SQLite WAL through the SQLite API, ensure no WAL sidecars or external readers remain, and atomically replace the live file with its encrypted copy. A temporary same-volume plaintext rollback file exists only during the final promotion and is retired after successful verification. On failure, the original is retained or restored.
5. If encryption is interrupted before promotion, the original plaintext database is still present and migration can be retried. If interrupted during promotion, inspect the legacy rollback and pre-encryption snapshot before continuing; never delete uncertain recovery data.
6. After opening an existing or newly created database, schema migrations run inside an immediate transaction. A verified encrypted \`recovery/before-schema-*.db\` snapshot is created first if the schema needs upgrading. On a failed migration the transaction rolls back.
7. Do not move, rename or manually edit WAL (\`-wal\`) or SHM (\`-shm\`) files of an open database. SQLite's online snapshot APIs handle committed WAL data.

## Manual encrypted backup

1. Log in as the **original owner administrator**, choose **النسخ الاحتياطي والاستعادة**.
2. Enter a strong independent recovery passphrase (minimum 16 characters; store securely **off the computer**).
3. Choose **إنشاء نسخة احتياطية مشفرة** and save \`.sobak\` to an external disk. Never overwrite a prior backup.
4. Use **اختيار نسخة ومعاينة محتواها** to check file integrity, correct passphrase, the number of schools, staff, students, submitted rounds, results, accounts and audit entries.
5. Do a separate test restore on an authorized machine and verify real records before deleting any old backup.

A \`.sobak\` is a SQLCipher-encrypted SQLite database, not a ZIP file or unencrypted SQLite copy. Opening it requires the recovery passphrase and the compatible encryption engine. Store it on offline media, preferably with physical access controls and a second copy off-site.

## Restore on the current machine

- Owner administrator selects the backup, provides its recovery passphrase and reviews the table counts.
- On explicit confirmation the application builds an encrypted candidate using the machine-specific DPAPI key, compares all records, checkpoints the current DB and performs a same-volume atomic replacement.
- An encrypted \`.pre-restore.db\` rollback of the preceding database is retained; on successful verification the application exits for a clean login.
- Before restoring a backup, independently save a verified backup of the most recent active records, because a restored snapshot may be older.

## Offline restore to a second authorized Windows computer

1. On the new Windows machine install/open the x86 or x64 distribution as appropriate. No internet is needed after installation; all native cipher binaries must be included.
2. During first-run setup choose **استعادة نسخة مشفرة من جهاز آخر**, select the \`.sobak\` file and enter its independently stored passphrase.
3. Confirm replacement of the genuinely empty local database. The program validates the encrypted backup, restores it and re-encrypts with a **new machine-bound DPAPI-protected key**.
4. Sign in with an existing restored user account. Restore preserves usernames, password hashes, roles, school bindings, permissions and audit history; it does not require copying the original machine's DPAPI key.

## Corrupt or unreadable active database

The application fails closed when the encrypted file cannot be opened. If the normal owner-only UI cannot start, **do not delete the database or its DPAPI key**. Close the program, safely copy the directory including any SQLite WAL/SHM sidecars while all connections are closed, preserve those files in an access-restricted external recovery archive, then use an authorized Windows installation/Windows profile to restore the last verified portable \`.sobak\` through first-run recovery. This is a documented offline recovery procedure, not an automatic in-place salvage of a damaged live database. If the protected key is lost, recover from the portable backup rather than resetting or weakening encryption.

## Automatic backups

- Owner administrator chooses destination directory and interval 1–168 hours, entering the independent backup passphrase.
- Configuration is wrapped using Windows DPAPI CurrentUser and requires the account's Windows profile. The scheduler checks every five minutes **while the app is running under the original owner administrator**; it is NOT a Windows service or a guaranteed background backup while the program is closed.
- Each successful automatic backup has a unique timestamped file name and is reopened/verified. Failures are visibly reported; no success status is recorded on failure.
- Backups are never silently pruned. The responsible owner must implement retention and free-space management, and periodically test off-device recovery.

## Security acceptance and maintenance

All of the following must pass before merging:

- Red/green regression tests: fresh encrypted DB, wrong key, lost DPAPI key, existing plaintext and WAL migration, interruption and rollback, exact school/student/staff/permissions/results/audit preservation, encrypted manual/automatic backup, wrong passphrase/tamper, preview and confirmed restore, recovery on different Windows account/computer, and native x86/x64 encryption smoke tests.
- \`Windows Core\` and \`Windows Release\` run independently against the **same commit**. Within Release, tests must pass **before** producing packages. Never publish or merge on failure.
- Exactly four independently downloadable packages: \`SchoolObservatory-x86-Portable.zip\`, \`SchoolObservatory-x86-Installer.exe\`, \`SchoolObservatory-x64-Portable.zip\`, \`SchoolObservatory-x64-Installer.exe\`.

### External implementation documentation

- SQLite3MC: https://utelle.github.io/SQLite3MultipleCiphers/docs/configuration/config_uri/
- SQLite3MC encryption pragmas: https://utelle.github.io/SQLite3MultipleCiphers/docs/configuration/config_sql_pragmas/
- SQLite backup API: https://sqlite.org/backup.html
- Microsoft Windows DPAPI: https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata

**Operational limitations:** Database encryption is not a replacement for Windows disk encryption, least-privilege user accounts, protected backups, or physical device security. Cross-machine data access is through **offline export and restore**, not live shared database access.
