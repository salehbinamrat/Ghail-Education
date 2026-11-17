# Phase 3 — Authoritative relationships and census reconciliation

## Version and invariants

Based on merged Phase 2 `main` at `05e02517bc776f676a79031f10d4db7ae1163739`. Phase 3 introduces no copied demographic or payroll tables. SQLCipher `user_version=4` is additive; existing encrypted v1/v2/v3 databases receive a verified pre-schema recovery snapshot and a transactional migration. Unsupported future schema versions are rejected before data changes. No original school, student, employee, academic-year or submission ID is regenerated.

- **School identity**: `schools.id` uniquely identifies an institution; directory profile, infrastructure, councils, classes, annual submissions and personnel records hold foreign keys to this ID.
- **Student identity**: `school_students.id` remains stable across transfer and promotion. `student_history` is the year, school and grade/section assignment. `student_school_codes` assigns a distinct *school-local code*, never a second student identity.
- **Employee identity**: `school_staff.id` and `employee_identities.id` refer to the same person; `original_payroll_school_id` identifies the employing institution; `employee_assignments.workplace_school_id` and dated intervals identify the place of duty. A transfer/secondment does not duplicate the person in another school's payroll list.
- **School grades**: `school_grades.id` is an actual registered academic-year group. Multiple sections of the same grade count individually. `school_physical_rooms` counts buildings/physical rooms and is not substituted for educational sections.
- **Official census**: `submissions` + `enrollment_rows` + `decisions` are approval-controlled snapshots. Live rosters never overwrite these historical official records.

## Exact metric contract

All operational counts are computed through read-only SQLite queries (not materialized synchronization tables). A save/archive/restore/transfer becomes visible on the next view/query refresh. The UI explicitly labels live and approved sources.

| Metric | Authoritative table(s) | Filters and formula | Academic period |
|---|---|---|---|
| Active schools | schools | COUNT where is_active=1, current role scope | Current |
| Active students | student_history, school_students | COUNT DISTINCT student_id; active identity, status=مسجل, end_utc IS NULL, school_id, academic_year | Selected year |
| Boys/girls | Same | Student count with student identity gender ذكور/إناث | Selected year |
| Sections | school_grades | COUNT registered grade/section IDs scoped to school/year, even if zero students | Selected year |
| Payroll staff / teachers | school_staff, employee_identities | COUNT DISTINCT active staff whose original_payroll_school_id matches selected school, optionally category=معلم | Current identity (historical payroll snapshots not recorded) |
| Working staff / teachers | school_staff, employee_assignments | COUNT DISTINCT active staff with current dated assignment at selected workplace, excluding إجازة; teachers filter category=معلم | At today's local date, **not** a past-year staffing reconstruction |
| Active parent council | school_council_members | COUNT school records where is_active=1 | Current |
| Submitted official forms | submissions, census_rounds | Latest revision per school and active census round; statuses submitted/school_approved; scoped access | Active year/round |
| Approved official forms | submissions, census_rounds | Latest revision per school/active round where status=approved | Active year/round |
| Approved enrollment | enrollment_rows, submissions | SUM boys+girls on approved form only | Submission's round/year |
| Approved teachers | submissions | male_teachers+female_teachers of approved submission | Submission's round/year |
| Approved educational sections | submissions | classrooms field is **educational section total**, not physical rooms | Submission's round/year |

The complete programmatic metric catalog is `EducationalMetricCatalog` in `Phase3Models.cs`. `GetSchoolOperationalSnapshot` is a scoped operational view; `GetCensusReconciliation` is a scoped comparison and returns per-school, per-grade, per-category data.

## Comparison behavior and ambiguity

- A missing approved census is **NULL** (unavailable), never an invented official zero.
- The difference for comparable metrics is `operational - approved`. Nonzero differences are flagged with source metadata. The approved submission ID is preserved for traceability.
- Reconciliation compares students in registered rosters, boys/girls, registered sections, and **current** actual working teachers against the latest approved census for the active academic year; payroll counts are displayed separately for context.
- In a historical year student placements and sections remain year-scoped; **present-day staffing is not treated as historical staffing**. No historical teacher difference is asserted without a dated staffing snapshot.
- A difference does **not** prove data corruption: official census counts may describe an earlier snapshot date. Investigation must compare the roster's effective date and the official census's snapshot.
- The reconciliation page shows school, category, year, current count, approved count, difference and status; selecting a row shows the source and formula. The review button requires an explicitly authorized reviewer, a reason and confirmation. It creates a dated audit event, **never adjusts an approved form**.

## Validation and protections

Schema v4 adds database-level triggers to reject UPDATE/DELETE of approved submissions; INSERT/UPDATE/DELETE of enrollment rows belonging to approved submissions; UPDATE/DELETE of approval decisions; retargeting a census round containing approvals into another academic year. The final approval's **decision INSERT remains allowed**, because it is recorded after the form reaches approved status.

Integrity indicators raise a reviewable mismatch against target zero for:
- orphan student identities, grades or years in placements;
- more than one simultaneously active placement for a student/year;
- payroll identity/assignment mismatch, broken school references and overlapping intervals;
- staff identities missing or inconsistent with originating payroll school;
- employee codes reused across distinct schools (manual review, no guessed merge).

SQLite primary keys and existing UNIQUE/FK/CHECK constraints continue to enforce new student IDs, school-specific codes, section/year uniqueness, and valid references on ordinary writes. Existing ambiguous legacy records remain visible; they are not silently coerced to valid or merged.

**Limitations:** Legacy staff with an undocumented real workplace may have a default original assignment lacking trustworthy effective dates. The current algorithm treats this as the stored assignment; cross-check its historical source manually before using it as a certified field census. Likewise, school-level staff identity metadata from legacy imports may contain unmatched codes. Reconciliation is a diagnostic, not an automatic approval mechanism.

## Regression and packaging gate

The Phase 3 xUnit acceptance tests verify add/edit/archive/restore, actual group counts, payroll versus physical workplace, deliberately mismatched approved submissions, missing approval, forbidden raw SQL modifications, v3-to-v4 migration preserving encrypted identity and creating recovery snapshots, authorization and audit, and deliberately tampered employment assignments. Phase 2 historical and security regressions remain enabled, including migration expectations adjusted to v4.

The required merge gate remains unchanged:

1. Create tests **before** implementation on a dedicated branch from newest main.
2. Start **Windows Core** and **Windows Release** independently and concurrently for the *same head SHA*.
3. In Release, restore/build/test including actual native x86/x64 encryption/restore checks **before** packaging.
4. Produce exactly `SchoolObservatory-x86-Portable.zip`, `SchoolObservatory-x86-Installer.exe`, `SchoolObservatory-x64-Portable.zip`, `SchoolObservatory-x64-Installer.exe`; validate independent artifacts, EXE/ZIP signatures and SHA-256.
5. Merge into main **only** if both workflows succeed on the same commit and four outputs pass their verification gate. A CI failure or missing output blocks merging.

Manual real-device launch, installer repair/uninstall and offline disaster recovery on genuine Windows x86 and x64 machines remain recommended before production deployment; GitHub CI package verification alone does not prove device compatibility.
