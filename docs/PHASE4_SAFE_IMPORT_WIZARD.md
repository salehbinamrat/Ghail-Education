# Phase 4 — Safe Excel, Word and CSV import

## Scope and previous baseline

Starts from Phase 3 merged `main` at `c8ca7c4e06ca819aadd2d9b4ef1e992f7688108f`. Phase 4 retains original school, student, employee, academic placement, results, attendance, evaluations, assignments, approvals and user IDs; the new SQLCipher v5 migration **adds** import batch tables. The existing school, staff and student import buttons now open the guided wizard instead of calling the old direct-upsert methods.

The implementation uses C# / .NET 10, SQLCipher and the existing WPF application. Excel and Word are read only as bounded, structured OOXML content. **Never execute document macros, links, formulas or code.**

## Five-stage workflow

1. **Select** a local file: .csv UTF-8, .xlsx or .docx. Files over 8 MB are rejected; CSV has an additional 5 MB bound.
2. **Identify** its format and first usable header row, then **map** source columns to normalized fields. Arabic/English header aliases are detected automatically; operators can override each mapping. Identifier columns are mandatory.
3. **Validate and preview** all rows as new, update, unchanged, duplicate, conflict or invalid. Each row identifies its original file row number and relevant header. Selecting a row shows field-by-field **before** and **after** values, including defaults for new records. Empty incoming values never erase existing fields. No database edits happen in these stages.
4. **Confirm** the counts and official school/year scope. If any row is invalid, duplicate or conflicting, the entire batch is blocked. On confirmation, the database reacquires a write transaction, repeats all validation and compares the original source and database state hashes to prevent stale-preview overwrites.
5. **Review results** showing created, updated, unchanged and rejected counts and an import batch ID. A permanent audit event records the user, source hash, source filename and total actions.

The same transaction commits the business records, immutable per-row before/after provenance, batch metadata and audit. A failure or interruption before commit cannot leave a partially imported batch.

## Supported educational records

| Type | Identity key | New rows | Existing rows | Safety constraints |
|---|---|---|---|---|
| Schools | case-insensitive school code | Required name, zone, ownership, stage; gender defaults mixed | Blank fields retained; stage and gender changes checked against registered students and grades | Only roles with school-management permission |
| Students | school + local student code | Name, valid active-year grade/registered section, gender | Corrects current originating-school identity and class without replacing results or attendance | Does not re-open archived identities, reconstruct moved students or create non-existent sections |
| Staff | payroll school + employee code | Identity and one initial payroll assignment | Updates only supplied fields on the same employee identity | Cross-school payroll code collisions are flagged; secondments must use the assignment workflow |
| Sections | school + academic year + grade + section | Adds registered academic group | Identical group is unchanged | No physical classroom duplication; closed-year writes forbidden |

An omitted `school_code` can use a deliberately selected school in the wizard. Source mapping cannot elevate a school-scoped user's authority.

## Protections

- The encrypted SQLite database rejects unknown future schema versions and makes a verified encrypted backup before any v4→v5 migration.
- Every data connection enables foreign-key integrity. Existing historical/approved-census database triggers remain enabled.
- Office packages are restricted by uncompressed total size, per-entry size, entry count, compression ratio, file paths and duplicate entries; external OOXML relationships, embedded VBA, workbook external links and formulas/error cells are rejected.
- Imported text that starts with spreadsheet formula operators (`=`, `+`, `-`, `@`) or contains control characters is invalid. Displayed columns are literal data; the wizard does not interpret or run them.
- Existing spreadsheet text may already have lost leading zeroes or precision *before export*. Preserve school/employee/student ID columns as **Text** before saving the source spreadsheet. The wizard never guesses lost identifiers.
- No silent replacement of missing values. Existing records with archived or transferred identities are classified as conflicts rather than revived/reassigned.

## Import history and rollback safety

Each batch records the user, filename, academic year, content hash, counts, timestamp and status. Every modified entity stores a before and after snapshot.

Automatic reversal is deliberately conservative. It is available for eligible **school and staff updates/creates**, and newly added unused sections. It requires the importing user or founding owner, relevant entity-management permissions, exact agreement between the current record and the post-import snapshot, and no later audit activity. New sections with active student enrollments cannot be removed; linked schools or staff cannot be deleted.

**Student import compensation is intentionally unavailable**: students may already have results, attendance, assessments or historical movements. Use authorized student edit/transfer/archive actions instead. The wizard must not silently erase that history just to undo an import.

Where reversal is blocked, the application displays the conflict and leaves the complete original batch untouched. There is no partial rollback.

## Safe edit, archive, restore, delete

Existing UI record editors remain permission-scoped. Schools can be archived/restored by administrators; students and staff can be marked inactive and restored without deleting their history; council membership uses its existing archive/restore workflow.

Permanent school deletion is now supported **only for the founding owner**, only after the school has been archived, only with an exact school code confirmation, and only when there are no school user accounts, official census submissions, student/employee/grade records, assignments, student histories, or import-batch references. Other linked tables are protected by SQLite foreign keys. Students and employees are never hard-deleted by the import wizard.

## Verification gate

- Tests first: classification, exact field differences, blank-safe updates, duplicate/conflicting/invalid rows, concurrency checks, protected school-scoped permissions, student and staff stable IDs, history preservation, rollback refused on later edits/dependent sections, archived-school-only deletion.
- Keep previous Phase 1–3 regression tests, with expected encrypted schema version updated to 5.
- Windows Core and Windows Release start concurrently on the final PR SHA.
- The Release workflow must build and pass its full xUnit suite, then pass real x86 and x64 SQLCipher backup/restore tests **before** package creation.
- Produce and verify exactly four standalone files: `SchoolObservatory-x86-Portable.zip`, `SchoolObservatory-x86-Installer.exe`, `SchoolObservatory-x64-Portable.zip`, `SchoolObservatory-x64-Installer.exe`, with ZIP/EXE signature and SHA-256 integrity.
- Merge only after both Core and Release succeed on the same head SHA, including `verify-all-4`; no bypass on failure.

Native CI is not a substitute for physical 32-bit and 64-bit Windows smoke tests, especially installer repair/uninstall, Microsoft SmartScreen and offline SQLCipher migration.
