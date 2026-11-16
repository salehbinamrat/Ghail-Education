# تقرير التحقق الأولي

تاريخ إعداد المصدر: 16 نوفمبر 2026

- XML WPF: SetupWindow.xaml
- XML WPF: LoginWindow.xaml
- XML WPF: App.xaml
- XML WPF: MainWindow.xaml
- XML project: SchoolObservatory.Tests.csproj
- XML project: SchoolObservatory.Desktop.csproj
- XML project: SchoolObservatory.Core.csproj
- XML project: SchoolObservatory.Data.csproj
- GitHub Actions YAML: windows-core.yml
- GitHub Actions YAML: windows-release.yml
- SQLite DDL: 8 tables compiled
- SQLite: school stage CHECK rejects invalid stage
- SQLite: case-insensitive school code uniqueness
- SQLite: school-scoped roles require school ID
- Automated C# test cases authored: 13 (not executed; includes inactive-session and school-scope regression tests)
- Actual .NET compilation, xUnit execution, Windows packaging: not verified (SDK unavailable)

**هذا تقرير تحقق ثابت للملفات وقيود SQL فقط. لا يثبت صحة تطبيق WPF وقت التشغيل.**
