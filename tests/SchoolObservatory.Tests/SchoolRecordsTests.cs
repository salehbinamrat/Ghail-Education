using System.IO.Compression;
using System.Text;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class SchoolRecordsTests
{
    private static (SqliteStore Db, UserAccount Owner, School A, School B, string Path) Setup()
    {
        var file = Path.Combine(Path.GetTempPath(), "school-records-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(file);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Strong-Founder-Password-123!");
        var owner = db.Authenticate("founder", "Strong-Founder-Password-123!")!;
        db.AddSchool(owner, "GH-01", "مدرسة البنات", "العليب", "حكومية", "أساسي", "إناث");
        db.AddSchool(owner, "GH-02", "مجمع تربوي", "المدينة", "حكومية", "مجمع تربوي", "مختلطة");
        var schools = db.GetSchools(owner);
        return (db, owner, schools.Single(s => s.Code == "GH-01"),
            schools.Single(s => s.Code == "GH-02"), file);
    }

    private static StaffImportRow Staff(string school, string code, string name,
        string category = "معلم") =>
        new(school, code, name, category, category == "معلم" ? "معلم" : "موظف",
            "رياضيات", "بكالوريوس", "ذكور");

    [Fact]
    public void SchoolGenderAndComplexStageSurviveMigrationAndReopen()
    {
        var (db, owner, a, b, path) = Setup();
        Assert.Equal("إناث", a.Gender);
        Assert.Equal("مجمع تربوي", b.Stage);
        db.Initialize();
        var second = new SqliteStore(path);
        second.Initialize();
        var schools = second.GetSchools(owner);
        Assert.Equal("إناث", schools.Single(s => s.Code == "GH-01").Gender);
        Assert.Equal("مجمع تربوي", schools.Single(s => s.Code == "GH-02").Stage);
        second.UpdateSchool(owner, a with { Gender = "ذكور", Stage = "ثانوي" });
        Assert.Equal("ذكور", second.GetSchools(owner).Single(s => s.Code == "GH-01").Gender);
        Assert.Throws<ArgumentException>(() =>
            second.AddSchool(owner, "BAD", "مدرسة", "المدينة", "حكومية", "أساسي", "غير معروف"));
    }

    [Fact]
    public void SchoolAccountIsLimitedToItsSchoolEvenThroughDirectDataApi()
    {
        var (db, owner, a, b, _) = Setup();
        db.CreateUser(owner, "view_a", "Strong-Viewer-Password-123!", Roles.Entry, a.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStaff));
        db.CreateUser(owner, "edit_a", "Strong-Editor-Password-123!", Roles.Entry, a.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStaff | PermissionFlags.ManageStaff));
        var viewer = db.Authenticate("view_a", "Strong-Viewer-Password-123!")!;
        var editor = db.Authenticate("edit_a", "Strong-Editor-Password-123!")!;
        Assert.Single(db.GetSchools(viewer));
        Assert.Single(db.GetSchools(editor));
        Assert.Empty(db.GetSchoolStaff(viewer, a.Id));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetSchoolStaff(viewer, b.Id));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetSchoolStaff(editor, b.Id));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.SaveSchoolStaff(viewer, new SchoolStaff("", a.Id, "001", "أحمد", "معلم", "معلم", "لغة عربية", "دبلوم", "ذكور", true)));
        db.SaveSchoolStaff(editor, new SchoolStaff("", a.Id, "001", "أحمد", "معلم", "معلم", "لغة عربية", "دبلوم", "ذكور", true));
        Assert.Single(db.GetSchoolStaff(viewer, a.Id));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.SaveSchoolStaff(editor, new SchoolStaff("", b.Id, "002", "زيد", "إداري", "وكيل", "", "", "ذكور", true)));
        Assert.Empty(db.GetSchoolStaff(owner, b.Id));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.UpdateOwnSchoolProfile(editor, b.Id, "ثانوي", "ذكور"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.UpdateOwnSchoolProfile(viewer, a.Id, "ثانوي", "ذكور"));
    }

    [Fact]
    public void SchoolEditorMayChangeItsOwnGenderAndStageWhenExplicitlyGranted()
    {
        var (db, owner, a, b, _) = Setup();
        db.CreateUser(owner, "profile_a", "Strong-Editor-Password-123!", Roles.Entry, a.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ManageSchoolProfile));
        var editor = db.Authenticate("profile_a", "Strong-Editor-Password-123!")!;
        db.UpdateOwnSchoolProfile(editor, a.Id, "مجمع تربوي", "مختلطة");
        Assert.Equal("مختلطة", db.GetSchools(editor).Single().Gender);
        Assert.Equal("مجمع تربوي", db.GetSchools(editor).Single().Stage);
        Assert.Equal("مجمع تربوي", db.GetSchools(owner).Single(s => s.Id == b.Id).Stage);
    }

    [Fact]
    public void SchoolCsvImportSupportsOptionalGenderAndLegacyColumns()
    {
        var withGender = SchoolCsvParser.Parse(
            "code,name,zone,ownership,stage,gender\nS1,الأولى,العليب,حكومية,مجمع تربوي,إناث\n");
        Assert.Equal("إناث", Assert.Single(withGender).Gender);
        var old = SchoolCsvParser.Parse(
            "code,name,zone,ownership,stage\nS2,الثانية,العليب,حكومية,أساسي\n");
        Assert.Equal("مختلطة", Assert.Single(old).Gender);
    }

    [Fact]
    public void PersonRecordsSupportTeachersAdministrativeStaffAndSocialWorkers()
    {
        var (db, owner, a, _, _) = Setup();
        foreach (var (code, category, title) in new[]
        {
            ("T-1", "معلم", "معلم رياضيات"),
            ("A-1", "إداري", "مدير"),
            ("S-1", "أخصائي اجتماعي", "أخصائي اجتماعي")
        })
            db.SaveSchoolStaff(owner, new SchoolStaff("", a.Id, code, "اسم " + code,
                category, title, "", "", "إناث", true));
        var records = db.GetSchoolStaff(owner, a.Id);
        Assert.Equal(3, records.Count);
        var teacher = records.Single(s => s.EmployeeCode == "T-1");
        db.SaveSchoolStaff(owner, teacher with { JobTitle = "معلم أول" });
        Assert.Equal("معلم أول", db.GetSchoolStaff(owner, a.Id).Single(s => s.EmployeeCode == "T-1").JobTitle);
        Assert.Throws<ArgumentException>(() =>
            db.SaveSchoolStaff(owner, teacher with { Category = "غير محدد" }));
    }

    [Fact]
    public void MultiSchoolImportRoutesByCodeAndIsAtomicOnUnknownCode()
    {
        var (db, owner, a, b, _) = Setup();
        var records = new[]
        {
            Staff("GH-01", "1", "عبدالله"),
            Staff("GH-02", "2", "فاطمة", "أخصائي اجتماعي")
        };
        Assert.Equal(2, db.ImportStaffRecords(owner, records, "sample.xlsx"));
        Assert.Single(db.GetSchoolStaff(owner, a.Id));
        Assert.Single(db.GetSchoolStaff(owner, b.Id));
        Assert.Throws<ArgumentException>(() =>
            db.ImportStaffRecords(owner, [Staff("GH-01", "3", "محمود"),
                Staff("NO-SUCH-SCHOOL", "4", "زيد")], "sample.docx"));
        Assert.Single(db.GetSchoolStaff(owner, a.Id));
    }

    [Fact]
    public void SchoolUserCannotImportDataForAnotherSchoolOrMergeCrossSchool()
    {
        var (db, owner, a, b, _) = Setup();
        db.CreateUser(owner, "staff_a", "Strong-Staff-Password-123!", Roles.Entry, a.Id,
            (long)(PermissionFlags.ViewSchools | PermissionFlags.ViewStaff | PermissionFlags.ManageStaff));
        var user = db.Authenticate("staff_a", "Strong-Staff-Password-123!")!;
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.ImportStaffRecords(user, [Staff("GH-01", "1", "سالم"),
                Staff("GH-02", "2", "علي")], "mixed.xlsx"));
        Assert.Empty(db.GetSchoolStaff(owner, a.Id));
        Assert.Empty(db.GetSchoolStaff(owner, b.Id));
        Assert.Equal(1, db.ImportStaffRecords(user, [Staff("GH-01", "1", "سالم")], "school.xlsx"));
    }

    [Fact]
    public void ExcelTableCanParseSharedStringsAndWordTableCanParseArabicHeaders()
    {
        var temp = Path.Combine(Path.GetTempPath(), "school-excel-" + Guid.NewGuid().ToString("N") + ".xlsx");
        using (var z = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Write(z, "xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1"><c r="A1" t="inlineStr"><is><t>school_code</t></is></c>
                   <c r="B1" t="inlineStr"><is><t>employee_code</t></is></c>
                   <c r="C1" t="inlineStr"><is><t>full_name</t></is></c>
                   <c r="D1" t="inlineStr"><is><t>category</t></is></c></row>
                  <row r="2"><c r="A2" t="s"><v>0</v></c><c r="B2"><v>007</v></c>
                   <c r="C2" t="inlineStr"><is><t>سالم عبدالله</t></is></c>
                   <c r="D2" t="s"><v>1</v></c></row>
                </sheetData></worksheet>
                """);
            Write(z, "xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <si><t>GH-01</t></si><si><t>معلم</t></si>
                </sst>
                """);
        }
        var excel = Assert.Single(SchoolStaffImportParser.Parse(temp));
        Assert.Equal("GH-01", excel.SchoolCode);
        Assert.Equal("007", excel.EmployeeCode);
        Assert.Equal("سالم عبدالله", excel.FullName);
        var word = Path.Combine(Path.GetTempPath(), "school-word-" + Guid.NewGuid().ToString("N") + ".docx");
        using (var z = ZipFile.Open(word, ZipArchiveMode.Create))
        {
            Write(z, "word/document.xml", """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:tbl>
                    <w:tr><w:tc><w:p><w:r><w:t>رمز المدرسة</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>الرقم الوظيفي</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>الاسم</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>الفئة</w:t></w:r></w:p></w:tc></w:tr>
                    <w:tr><w:tc><w:p><w:r><w:t>GH-02</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>900</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>منى صالح</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>إداري</w:t></w:r></w:p></w:tc></w:tr>
                  </w:tbl></w:body>
                </w:document>
                """);
        }
        var docx = Assert.Single(SchoolStaffImportParser.Parse(word));
        Assert.Equal("GH-02", docx.SchoolCode);
        Assert.Equal("إداري", docx.Category);
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        using var stream = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
        stream.Write(content);
    }
}
