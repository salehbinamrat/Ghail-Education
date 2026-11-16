using System.IO.Compression;
using System.Text;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class StudentRecordsTests
{
    private static (SqliteStore Store, UserAccount Owner, School Boys, School Mixed, string Path) Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "school-students-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteStore(path);
        store.Initialize();
        store.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Secure-Founder-Password-123!");
        var owner = store.Authenticate("founder", "Secure-Founder-Password-123!")!;
        store.AddSchool(owner, "B01", "مدرسة البنين", "العليب", "حكومية", "أساسي", "ذكور");
        store.AddSchool(owner, "M01", "المجمع", "المدينة", "حكومية", "مجمع تربوي", "مختلطة");
        var schools = store.GetSchools(owner);
        return (store, owner, schools.Single(x => x.Code == "B01"),
            schools.Single(x => x.Code == "M01"), path);
    }

    private static SchoolStudent Student(School school, string code, string name, int grade = 3,
        string section = "أ", string gender = "") =>
        new("", school.Id, code, name, grade, section, gender, "ولي الأمر",
            "777000000", "بيانات تجريبية", true, "2026/2027");

    [Fact]
    public void GradesAreIsolatedAndValidatedAgainstSchoolStage()
    {
        var (store, owner, boys, mixed, _) = Create();
        var one = store.AddSchoolGrade(owner, boys.Id, 3, "أ");
        Assert.Equal("أ", one.Section);
        Assert.Single(store.GetSchoolGrades(owner, boys.Id));
        Assert.Throws<ArgumentException>(() => store.AddSchoolGrade(owner, boys.Id, 12, "أ"));
        store.AddSchoolGrade(owner, mixed.Id, 12, "ب");
        Assert.Single(store.GetSchoolGrades(owner, mixed.Id));
        Assert.Single(store.GetSchoolGrades(owner, boys.Id));
        Assert.Throws<ArgumentException>(() => store.AddSchoolGrade(owner, boys.Id, 3, "../../"));
    }

    [Fact]
    public void SingleGenderSchoolDerivesGenderAndMixedSchoolRequiresChoice()
    {
        var (store, owner, boys, mixed, path) = Create();
        store.AddSchoolGrade(owner, boys.Id, 3, "أ");
        store.SaveStudent(owner, Student(boys, "0007", "سالم"));
        var saved = Assert.Single(store.GetStudents(owner, boys.Id));
        Assert.Equal("ذكور", saved.Gender);
        Assert.Equal("0007", saved.StudentCode);
        Assert.Throws<ArgumentException>(() => store.SaveStudent(owner,
            Student(boys, "0008", "خالد", gender: "إناث")));
        store.AddSchoolGrade(owner, mixed.Id, 3, "أ");
        Assert.Throws<ArgumentException>(() =>
            store.SaveStudent(owner, Student(mixed, "0001", "منى")));
        store.SaveStudent(owner, Student(mixed, "0001", "منى", gender: "إناث"));
        store.Initialize();
        var reopened = new SqliteStore(path);
        reopened.Initialize();
        Assert.Equal("ذكور", reopened.GetStudents(owner, boys.Id).Single().Gender);
        Assert.Equal("إناث", reopened.GetStudents(owner, mixed.Id).Single().Gender);
    }

    [Fact]
    public void AStudentHasResultsAssessmentsAndAttendanceWithoutAffectingOtherSchools()
    {
        var (store, owner, boys, mixed, _) = Create();
        store.AddSchoolGrade(owner, boys.Id, 3, "أ");
        store.SaveStudent(owner, Student(boys, "0001", "محمد"));
        var student = Assert.Single(store.GetStudents(owner, boys.Id));
        store.SaveStudentResult(owner, boys.Id, student.Id, "الفصل الأول", "الرياضيات", 87, 100, "جيد");
        store.SaveStudentResult(owner, boys.Id, student.Id, "الفصل الأول", "الرياضيات", 90, 100, "تحسن");
        store.AddStudentAssessment(owner, boys.Id, student.Id, "المشاركة", "ممتاز", "نشط");
        store.RecordStudentAttendance(owner, boys.Id, student.Id,
            new DateOnly(2026, 11, 17), "حاضر", "");
        Assert.Equal(90, Assert.Single(store.GetStudentResults(owner, boys.Id, student.Id)).Score);
        Assert.Single(store.GetStudentAssessments(owner, boys.Id, student.Id));
        Assert.Single(store.GetStudentAttendance(owner, boys.Id, student.Id));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.GetStudentResults(owner, mixed.Id, student.Id));
        Assert.Throws<ArgumentException>(() =>
            store.SaveStudentResult(owner, boys.Id, student.Id, "الفصل الأول", "الرياضيات", 110, 100, ""));
    }

    [Fact]
    public void ViewOnlyAccountCannotCreateStudentOrWriteResultsAndCannotAccessForeignSchool()
    {
        var (store, owner, boys, mixed, _) = Create();
        store.AddSchoolGrade(owner, boys.Id, 3, "أ");
        store.SaveStudent(owner, Student(boys, "A01", "سالم"));
        var schoolStudent = Assert.Single(store.GetStudents(owner, boys.Id));
        store.CreateUser(owner, "reader01", "Secure-Viewer-Password-123!", Roles.Entry, boys.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStudents));
        var reader = store.Authenticate("reader01", "Secure-Viewer-Password-123!")!;
        Assert.Single(store.GetStudents(reader, boys.Id));
        Assert.Throws<UnauthorizedAccessException>(() => store.GetStudents(reader, mixed.Id));
        Assert.Throws<UnauthorizedAccessException>(() => store.GetStudent(reader, mixed.Id, schoolStudent.Id));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.SaveStudent(reader, Student(boys, "A02", "زيد")));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.AddSchoolGrade(reader, boys.Id, 2, "أ"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.SaveStudentResult(reader, boys.Id, schoolStudent.Id, "الفصل الأول", "العربية", 80, 100, ""));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.ImportStudents(reader, boys.Id,
                [new StudentImportRow("B01", "R02", "سارة", 3, "أ", "", "", "")], "students.xlsx"));
    }

    [Fact]
    public void RestrictedEditorCannotWriteAnotherSchoolsResultsOrImport()
    {
        var (store, owner, boys, mixed, _) = Create();
        store.AddSchoolGrade(owner, boys.Id, 2, "أ");
        store.AddSchoolGrade(owner, mixed.Id, 2, "أ");
        store.SaveStudent(owner, Student(mixed, "M1", "منى", 2, gender: "إناث"));
        var foreign = Assert.Single(store.GetStudents(owner, mixed.Id));
        store.CreateUser(owner, "editor01", "Secure-Editor-Password-123!", Roles.Entry, boys.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStudents |
                    PermissionFlags.ManageStudents | PermissionFlags.ManageStudentEvaluation));
        var editor = store.Authenticate("editor01", "Secure-Editor-Password-123!")!;
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.SaveStudentResult(editor, mixed.Id, foreign.Id, "الفصل الأول", "العلوم", 70, 100, ""));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.SaveStudent(editor, Student(mixed, "M2", "زيد", 2, gender: "ذكور")));
        Assert.Throws<ArgumentException>(() =>
            store.ImportStudents(editor, boys.Id,
                [new StudentImportRow("M01", "M2", "زيد", 2, "أ", "ذكور", "", "")], "wrong.xlsx"));
        Assert.Empty(store.GetStudents(owner, boys.Id));
    }

    [Fact]
    public void StudentImportIsAtomicForInvalidRowsAndUpdatesOnlySameSchool()
    {
        var (store, owner, boys, mixed, _) = Create();
        store.AddSchoolGrade(owner, boys.Id, 4, "أ");
        var good = new StudentImportRow("B01", "009", "خالد", 4, "أ", "", "ولي", "777111222");
        var wrong = new StudentImportRow("B01", "010", "لا يوجد صف", 7, "ب", "", "", "");
        Assert.Throws<ArgumentException>(() =>
            store.ImportStudents(owner, boys.Id, [good, wrong], "sample.docx"));
        Assert.Empty(store.GetStudents(owner, boys.Id));
        Assert.Equal(1, store.ImportStudents(owner, boys.Id, [good], "sample.xlsx"));
        Assert.Equal(1, store.ImportStudents(owner, boys.Id,
            [good with { FullName = "خالد عبدالله" }], "sample.xlsx"));
        Assert.Equal("خالد عبدالله", Assert.Single(store.GetStudents(owner, boys.Id)).FullName);
        Assert.Throws<ArgumentException>(() => store.ImportStudents(owner, boys.Id,
            [good, good], "duplicate.xlsx"));
        Assert.Single(store.GetStudents(owner, boys.Id));
    }

    [Fact]
    public void ExcelAndWordStudentTableParseWithArabicHeadersAndNoGenderColumn()
    {
        var xlsx = Path.Combine(Path.GetTempPath(), "students-" + Guid.NewGuid().ToString("N") + ".xlsx");
        var docx = Path.Combine(Path.GetTempPath(), "students-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            using (var zip = ZipFile.Open(xlsx, ZipArchiveMode.Create))
                Write(zip, "xl/worksheets/sheet1.xml", """
                    <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                    <row r="1">
                    <c r="A1" t="inlineStr"><is><t>رمز الطالب</t></is></c>
                    <c r="B1" t="inlineStr"><is><t>اسم الطالب</t></is></c>
                    <c r="C1" t="inlineStr"><is><t>الصف</t></is></c>
                    <c r="D1" t="inlineStr"><is><t>الشعبة</t></is></c></row>
                    <row r="2">
                    <c r="A2" t="inlineStr"><is><t>007</t></is></c>
                    <c r="B2" t="inlineStr"><is><t>عمر سعيد</t></is></c>
                    <c r="C2"><v>3</v></c><c r="D2" t="inlineStr"><is><t>أ</t></is></c></row>
                    </sheetData></worksheet>
                    """);
            var excel = Assert.Single(StudentImportParser.Parse(xlsx));
            Assert.Equal("007", excel.StudentCode);
            Assert.Equal("", excel.Gender);
            using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
                Write(zip, "word/document.xml", """
                    <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:tbl>
                    <w:tr><w:tc><w:p><w:r><w:t>رقم الطالب</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>الاسم</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>الصف</w:t></w:r></w:p></w:tc></w:tr>
                    <w:tr><w:tc><w:p><w:r><w:t>008</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>علي سالم</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>5</w:t></w:r></w:p></w:tc></w:tr>
                    </w:tbl></w:body></w:document>
                    """);
            var word = Assert.Single(StudentImportParser.Parse(docx));
            Assert.Equal(5, word.Grade);
            Assert.Equal("أ", word.Section);
        }
        finally { File.Delete(xlsx); File.Delete(docx); }
    }

    private static void Write(ZipArchive zip, string path, string xml)
    {
        using var file = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        file.Write(xml);
    }
}
