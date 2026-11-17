# Phase 5 — Professional WPF UX regression matrix

Scope: presentation and incremental MVVM only, based on Phase 4 merge 97a438cf. No data migrations, changed permissions, academic history mutation, or unsafe import changes.

## Architecture
- Styles/ProfessionalTheme.xaml: semantic contrast, Arabic Arial Bold, cards, forms, navigation and errors.
- Views/DashboardView.xaml and ViewModels/DashboardViewModel.cs: read-only dashboard with async loading and scoped actions.
- Services/DashboardService.cs: canonical source for statistics; never duplicate approved submissions in the current student/staff counts.
- Phase5UiModels.cs: pure navigation, responsiveness, school-form validation and dashboard composition.
- Legacy forms retain functional handlers during gradual MVVM migration.

## Page-by-page inspection matrix
| Page / window | Confirm all controls | Permissions | Safety regression |
| --- | --- | --- | --- |
| Initial setup | first owner, encrypted recovery, focus, validation | Founder only | No default credentials |
| Login | username/password, remember/forget, Enter, error | Authenticated active user | Inline error; no credential in drafts |
| لوحة التحكم | metric cards, refresh, report links, progress | Scoped statistics | No duplicate totals |
| دليل المدارس | add, edit, archive, restore, search, open profile | ViewSchools/ManageSchools | No silent record loss |
| School profile | details, staff and linked records | School scope | Stable IDs |
| كادر المدارس | search, sorting, edit, assignment | ViewStaff/ManageStaff | No duplicated employee identity |
| Staff profile | history, edit, archive, restore | Staff scope | Preserve payroll history |
| الطلاب والصفوف | school, grade, section, search, export, print, edit | ViewStudents/ManageStudents | No cross-school updates |
| Student profile | grades, results, evaluations, attendance, history | Evaluation rights | Maintain selected tab |
| Academic years | open/close/history | Allowed managers | Never edit closed history |
| Historical students | promote, transfer, archive | School scope | Chronology preserved |
| الإحصاء | school/grade totals, approval submission | EditCensus | Recompute and validate totals |
| Census review | school/district approve, return reason | Approval role | Immutable approvals |
| Reconciliation | mismatches and canonical source | ViewReports+relevant scope | Read-only |
| Reports | read, sort, export CSV | ViewReports | Formula-injection protection |
| الاستيراد | CSV/XLSX/DOCX, map columns, preview, confirm | Scoped import | All-or-nothing and audit |
| Import history | diff and reversal | Importer/owner permission | No later-change overwrite |
| Users | account, role, school, permission flags | Delegated/owner | No elevation of privileges |
| Audit | sort and inspect | ViewAudit | No delete action |
| Backup | encrypted export/restore | Owner only | Verified recovery |

## Display matrix
1. Windows 1024 × 768 at 100%, 125%, 150% scaling: tab through every dialog and toolbar. Test compact menu toggle and no inaccessible buttons.
2. Windows 1366 × 768 at 100%, 125%, 150% scaling: login, initial setup, large import preview, grade tabs, every form and wide table.
3. Windows 1920 × 1080 at 100%, 125%, 150% scaling: ensure cards wrap and whitespace remains balanced.
4. Windows 800 × 600 if supported: work area and footer controls remain accessible; no clipped labels or form overlays.
5. Resize and change DPI with app open; evaluate long Arabic labels, high-contrast selection, RTL alignment and button spacing.

## Interaction / accessibility
- لوحة المفاتيح: Tab, Shift+Tab, Enter, Space, arrows and Esc; check focus order, screen-reader accessible labels and no traps.
- Check Arabic RTL headings, Arial Bold, readable contrast, selected row and keyboard focus on buttons and editable cells.
- Use non-modal inline errors for school edits; fix error and retry without losing entered data.
- Switch between school/student/staff searches, navigate to a profile and back; check relevant in-session filter/selection persistence.
- Trigger slow dashboard refresh; observe progress, verify other pages remain usable and no late response replaces the active page.
- Verify school viewer cannot see or execute forbidden edit, approval, import, user administration, deletion.
- Test empty searches, invalid files, interrupted 5000-row imports, later-activity reversal refusal and historical protection.
- Test direct x86/x64 Windows launch and installer setup/uninstall manually; CI build success alone does not prove pixel-level compatibility.

## Mandatory CI gate
- Windows Core and Windows Release must both finish successfully at the same PR head SHA.
- Release test job runs xUnit and native x86/x64 SQLCipher before any packages.
- Four separately verified outputs: SchoolObservatory-x86-Portable.zip, SchoolObservatory-x86-Installer.exe, SchoolObservatory-x64-Portable.zip, SchoolObservatory-x64-Installer.exe.
- Verify executable/ZIP headers, four files, SHA-256 and verify-all-4.
- Stop on any CI failure; no merge until gates pass. Manual test matrix must be completed on an actual Windows device before claiming full visual acceptance or production readiness.

Out of scope: Phase 6, data migrations and a full rewrite of all WPF code-behind.
