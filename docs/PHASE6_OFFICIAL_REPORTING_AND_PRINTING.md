# Phase 6 — Professional reporting and print system

**Source:** Phase 5 merged `main` SHA `a91ec7fa26038defc063cc822c6bdd6e591ff5ba`.

## Sources and meaning
- School directory: `GetSchools` scoped to actor.
- Enrollment: canonical current or historical year-specific student placements; counts grouped by grade, section, gender, **not** approved census submissions.
- Grades/sections: school year-specific `school_grades` plus permitted enrolled counts, not physical rooms.
- Student results: current and transferred results by requested academic year. Only school student managers can export personally identifying data.
- Attendance/absence: current and transferred attendance by **explicit** academic year; legacy attendance with NULL academic year excluded rather than guessed.
- Staff: current employee directory and current administrative category / assignment status; **historical staff allocation is not reconstructed from current records**.
- Secondments: current workplace and payroll school as provided by authorized directory.
- Parent councils: protected personal names and numbers require school profile management permission.
- Census: year-specific last submission per school. **Only status `approved` is described as approved.** Unapproved draft/submitted/returned status is explicit in title and every exported medium. Grade-level filtering does not misallocate school-wide teacher totals to a grade.
- Reconciliation and missing profile fields: read-only derived report.
- All exported values come from permission-checked read-only queries; all filters operate on authorized school scope. No database writes/migrations.

## Controls
Choose one of 11 report categories, school, zone, academic year, stage, grade/section, mixed-school gender, staff category, employment and assignment state, and approval status as relevant.
Press **عرض التقرير وتطبيق المرشحات** to refresh. **CSV**, **XLSX**, and **معاينة PDF والطباعة** use the same authorized read snapshot. The print preview uses A4 with RTL rows, Arial Bold, page headers, footers, page numbering and no silent row clipping. Landscape is automatic for wide tables but user may override it. Custom optional heading and footer are print-layout options only; they never alter source data or approval.

## Provisional government templates
No official government-approved reporting template was supplied. Every PDF, Excel and CSV includes “قالب مبدئي غير معتمد كوثيقة حكومية”. Source data may separately be approved after the school/district approval workflow. This **does not certify** the template or make a draft official.

## Export safety
- XLSX generated as SpreadsheetML ZIP with RTL sheet direction, Arabic headers, numeric totals, A4 page setup, auto-filter and frozen headers. User-entered text is always `inlineStr`, with potential formula prefixes guarded. No formula nodes are emitted.
- CSV uses existing UTF-8 BOM and formula prefix escaping, plus full title/institution/year/date/filters/source status/record totals metadata. Export files are written atomically.
- PDF uses PDFsharp-WPF **6.2.4** to embed rendered WPF pages (raster image PDF): Arabic shaping is retained, but **PDF text is not selectable/searchable**. No false claim that a page-image PDF is searchable or accessible. PDF views and printers require live Windows interaction for visual acceptance.
- Report preview intentionally limits pages to 5000 records; longer detail reports must narrow filters. XLSX supports up to 100000 rows, subject to memory.

## Acceptance test protocol
1. Run data contract tests comparing school, student, staff, approved census against store queries.
2. Run Windows STA Arabic PDF smoke and check reader can parse multiple A4 pages.
3. Inspect XLSX in Excel/LibreOffice; use OpenXML schema validator when available. Check Arabic RTL, numbers, wide tables, no formula execution.
4. Test A4 print preview on Windows at 100%, 125%, 150% scaling, physical printers and PDF printers, portrait and landscape. Verify first/last pages, repeated headers, row wrapping and page numbers.
5. Test principal, scoped operator, viewer, manager, reviewer and founder; cross-school export and personal information never escapes authorization.
6. Confirm a draft/returned census cannot print or export as an approved official document.
7. Confirm **Windows Core** and **Windows Release** pass on identical final head SHA; Release tests before packaging; x86/x64 Portable + Installer all independently verified; no merge on failure.

No Phase 7 work or changes to database schema occur in this phase.
