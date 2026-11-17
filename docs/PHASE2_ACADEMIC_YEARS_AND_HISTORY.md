# Phase 2 — Academic years and educational history

## Scope and release gate

The original Phase 2 update advanced encrypted SQLCipher databases from schema version 1 to version 2. The verified Phase 2 hardening upgrade advances version 2 to version 3, without rebuilding existing student or staff identities, accounts, passwords, census data, assessment records or audit events. Windows Core and Windows Release must both pass, including x86/x64 native security tests, before publishing the four independent Portable/Installer files.

## Academic-year lifecycle

Only the authenticated founding owner may open, activate or close a year.

- **Open**: creates a new year and inactive census round, without changing the current year.
- **Activate**: atomically updates the active year, settings, and census round. A partial unique index ensures only one active academic year.
- **Close**: closes a non-active year and protects official submissions, census detail rows, approvals, classes, student histories, results, assessments and attendance via database triggers.
- **Browse**: historical rosters are accessible read-only and enforce the same school-level visibility rules.

A year closure never deletes records and never promotes students automatically. Schema v3 also rejects attempts to **retarget** an open-year record to a closed year, alter census-round year relationships, rename or delete a closed year, or reopen it by direct SQL. Both the previous and new academic-year relationships are guarded on UPDATE.

## Students and school facilities

The Phase 1 student ID stays permanent. Separate school-local codes prevent code collisions upon transfer. Dated enrollment history and movement events support promotion, repeating, graduation, school and section transfer, and withdrawal; a duplicate-year constraint and movement guards prevent duplicate promotion. Past results and attendance remain attached to the original school, while new destination-school records use separate year-scoped tables.

The academic stage snapshot, grade, section, and physical classroom are distinct. A classroom may be assigned to sections on different shifts but cannot be double-booked in the same shift and year.

School profiles include administrative contacts, geography, buildings, utilities, rooms, shifts and facilities; existing father/mother councils retain names, positions and phone numbers.

**Legacy attendance uncertainty:** Phase 1 attendance was not year-tagged. Phase 2 preserves the records and leaves their year NULL rather than inventing historical membership. New attendance records are year-tagged.

## Employees and assignments

The legacy employee ID is retained in a normalized identity record with original payroll school, qualification, specialization, and employment/contract/qualification years. Separate dated assignment records represent duty location, secondment, transfer and loans, without inserting a second employee into the staff register. Overlapping assignment periods are rejected. A cross-school assignment requires permission at both origin and destination.

Historical legacy workplace notes remain available from Phase 1. Ambiguous employee codes or missing assignment destinations are not auto-merged or assigned guessed dates; they require manual verification.

## Authorization and historical safeguards

- Changing the active academic year requires the founding owner's current authenticated account.
- Student rosters, histories, evaluations, school rooms, facilities and personnel records enforce school-scoped permissions.
- Transferring students and employees between schools requires write access to both schools.
- A destination-only student editor can manage permitted enrollment transitions, but may not rewrite another school's permanent student identity.
- All authorized transitions create audit events and retain stable IDs.

## Encrypted migration and rollback

1. Read the existing SQLite user_version and reject future versions before changing WAL or schema.
2. Create and verify a SQLCipher-encrypted pre-schema snapshot in the protected recovery directory.
3. Apply additive DDL and deterministic backfills within one immediate transaction; preserve legacy tables and identifiers.
4. Backfill academic years, local codes, placements and employee identities only when missing. Do not invent missing dates.
5. The original historical-data migration marks schema version 2. Schema v3 installs additional closed-year integrity triggers, creates a verified encrypted pre-migration snapshot when upgrading any existing v1/v2 database, and marks version 3 in the same transaction. On error, roll back and preserve the original database plus snapshot.

Never delete the encrypted database, DPAPI key or protected recovery snapshot during an upgrade. Keep a separate encrypted recovery copy and test offline restoration on a second PC.

## Verification and limits

Automated tests cover year transitions, closed-record locks, attempts to move results/attendance/assessments, classes or census submissions into closed years, attempts to rename/delete/reopen closed years, encrypted v2-to-v3 migration with all student IDs and results preserved, promotion idempotency, same-code school transfers, school boundaries, chained assignments, historical rosters, and preservation of old identifiers. Native encryption checks run on both Windows architectures. Packaging is blocked whenever a required test fails.

Tests cannot prove that every installer launches on every physical Windows machine. Perform real-device smoke tests on Windows x86/x64, including SQLCipher migration, restore, Portable start and Installer repair/uninstall, before entering production educational information. Existing ambiguous staff identity records must be reconciled with verifiable evidence; never coalesce them simply by a shared code.
