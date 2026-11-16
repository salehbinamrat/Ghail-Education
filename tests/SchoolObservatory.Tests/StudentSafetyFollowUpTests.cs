using System.IO.Compression;
using System.Text;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class StudentSafetyFollowUpTests
{
    private static (SqliteStore Store, UserAccount Owner, School School) Setup()
    {
        var path = Path.Combine(Path.GetTempPath(), "student-safety-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteStore(path);
        store.Initialize();
        store.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Secure-Founder-Password-123!");
        var owner = store.Authenticate("founder", "Secure-Founder-Password-123!")!;
        store.AddSchool(owner, "T01", "مدرسة اختبار", "العليب", "حكومية", "أساسي", "ذكور");
        var school = Assert.Single(store.GetSchools(owner));
        store.AddSchoolGrade(owner, school.Id, 3, "أ");
        return (store, owner, school);
    }

    [Fact]
    public void InactiveStudentCannotReceiveNewResultsAssessmentsOrAttendance()
    {
        var (store, owner, school) = Setup();
        store.SaveStudent(owner, new SchoolStudent("", school.Id, "001", "طالب تجريبي", 3, "أ",
            "", "", "", "", true, "2026/2027"));
        var student = Assert.Single(store.GetStudents(owner, school.Id));
        store.SaveStudentResult(owner, school.Id, student.Id, "الفصل الأول", "العلوم", 80, 100, "");
        store.SaveStudent(owner, student with { IsActive = false });
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveStudentResult(owner, school.Id, student.Id, "الفصل الأول", "العلوم", 95, 100, ""));
        Assert.Throws<InvalidOperationException>(() =>
            store.AddStudentAssessment(owner, school.Id, student.Id, "السلوك", "ممتاز", ""));
        Assert.Throws<InvalidOperationException>(() =>
            store.RecordStudentAttendance(owner, school.Id, student.Id,
                new DateOnly(2026, 11, 17), "حاضر", ""));
        Assert.Equal(80, Assert.Single(store.GetStudentResults(owner, school.Id, student.Id)).Score);
    }

    [Fact]
    public void ExcelStudentImportRejectsDuplicateHeaderRowsInOneSheet()
    {
        var path = Path.Combine(Path.GetTempPath(), "students-headers-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open(),
                    new UTF8Encoding(false));
                w.Write("""
                    <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                    <row r="1"><c r="A1" t="inlineStr"><is><t>رقم الطالب</t></is></c>
                    <c r="B1" t="inlineStr"><is><t>اسم الطالب</t></is></c>
                    <c r="C1" t="inlineStr"><is><t>الصف</t></is></c>
                    <c r="D1" t="inlineStr"><is><t>student_code</t></is></c></row>
                    <row r="2"><c r="A2" t="inlineStr"><is><t>0001</t></is></c>
                    <c r="B2" t="inlineStr"><is><t>طالب</t></is></c>
                    <c r="C2"><v>3</v></c></row>
                    </sheetData></worksheet>
                    """);
            }
            Assert.Throws<ArgumentException>(() => StudentImportParser.Parse(path));
        }
        finally { File.Delete(path); }
    }
}
