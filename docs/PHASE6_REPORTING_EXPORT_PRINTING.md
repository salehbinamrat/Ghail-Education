# Phase 6 — Reporting, XLSX, PDF, and print preview

## Entry point

Open **التقارير والتصدير** in the Windows application. Select report type,
school, district zone, academic year and relevant student/staff filters.
Use **تحديث ومعاينة التقرير**, **تصدير Excel XLSX**, **تصدير PDF** or
**معاينة قبل الطباعة**. Print preview uses an embedded WPF A4 landscape
DocumentViewer with no temporary report file; sensitive rosters remain in
memory until the preview window is closed. The operator reviews pages
before invoking the viewer's normal print action. The application does not
silently send records to a printer or network service.

## Sources and trust boundaries

- School directory: `schools`, accessed through `GetSchools`.
- Student enrollment/grades/sections: authenticated rosters and historical placements.
- Results and attendance: existing student results and year-qualified attendance APIs.
- Staff and assignments: district staff directory, with canonical payroll school identity; actual assignment codes (انتداب / تكليف / إعارة) are normalized to the personnel report labels (منتدب / مكلف / معار). A legacy original assignment is not proof of direct duty.
- Councils: school-scoped council records; exporting telephone numbers requires `ViewStaff`.
- Approved census: latest *approved* snapshot **per school and academic year** from
  `submissions`, `census_rounds`, and `enrollment_rows`. A later draft
  never replaces a previously approved snapshot in this report.
- Operational census: latest revision and status for the requested academic year; **always unapproved as a report**,
  even if it contains a status column indicating an approved source row.
- Inconsistencies: existing Phase 3 reconciliation against authoritative source tables.

The service requires `ViewReports` and `ViewSchools`, and also checks
`ViewStudents` or `ViewStaff` for private student/staff records. School-scoped
principals and entry users cannot request another school's records. Export
re-evaluates the authentication context rather than trusting stale preview rows.

## Approval terminology

**Operational report — not officially approved** is the default.
The approval label is applied only to an actual fully approved census snapshot.
The output layouts are **provisional, configurable templates**, not a claim
that Ministry of Education or government-approved forms have been obtained.
To adopt a government template, obtain the authoritative signed form,
version, dimensions, column rules, identifiers and approval workflow,
then compare the implementation against those requirements.

## File output

- Excel: `ClosedXML`, native XLSX, Arabic right-to-left worksheet, styled
  headings and totals, A4 landscape print setup, page numbers, explicit
  string/text cells, and formula-prefix neutralization.
- PDF: `QuestPDF`, A4 landscape, Arabic right-to-left layout and Arial
  font, repeated table header, margins, generation timestamp, printed filters,
  totals and page numbers.
- PDF library licensing must be reviewed against QuestPDF's published terms
  before external/commercial distribution; the source currently selects
  Community licensing.

## Automated checks

- Mandatory Windows Core and Windows Release run independently for pull
  requests and branch pushes.
- Release executes restore/build/tests, then x86/x64 native SQLCipher
  recovery checks *before* the package matrix.
- Four deliverables: x86 Portable, x86 Installer, x64 Portable, x64 Installer.
- Phase 6 tests cover row-shape validation, approval labels, CSV/XLSX
  formula prefixes, role/school scopes, report totals versus SQLite,
  Arabic XLSX round-trip, and PDF signature generation.
- A green pipeline is required; a run which is queued, incomplete or failed
  does not authorize merging to `main`.

## Known format and data limitations

Current staff directory reflects effective *current* personnel assignments,
not an independently persisted historic employee assignment snapshot. Do not
describe a prior academic year's personnel roster as historically certified.
Attendance is selected by the stored academic-year field rather than using
calendar-year prefix matching. Extremely wide official government layouts
should be configured only against verified model forms.
