using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class ImportTests
{
    [Fact] public void ReadsQuotedArabicSchoolNames()
    {
        var rows = SchoolCsvParser.Parse("code,name,zone,ownership,stage\n" +
            "100,\"مدرسة, الأمل\",شرق,حكومية,أساسي\n");
        var school = Assert.Single(rows);
        Assert.Equal("مدرسة, الأمل", school.Name);
    }

    [Fact] public void DuplicateSchoolCodesInImportAreRejected()
    {
        Assert.Throws<ArgumentException>(() => SchoolCsvParser.Parse(
            "code,name,zone,ownership,stage\n" +
            "1,الأولى,شمال,حكومية,أساسي\n" +
            "1,الثانية,جنوب,حكومية,ثانوي\n"));
    }

    [Fact]
    public void InvalidUtf8CannotSilentlyCorruptArabicSchoolNames()
    {
        // An invalid UTF-8 continuation must be rejected instead of producing replacement glyphs.
        var bad = new byte[] { (byte)'c', (byte)'o', (byte)'d', (byte)'e', (byte)',', 0xC3, 0x28 };
        Assert.Throws<System.Text.DecoderFallbackException>(() => SchoolCsvParser.ParseUtf8(bad));
    }

    [Fact]
    public void ValidUtf8SchoolNamesAndBomRemainSupported()
    {
        var csv = "code,name,zone,ownership,stage\n1,مدرسة العليب,العليب,حكومية,أساسي\n";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray();
        Assert.Equal("مدرسة العليب", Assert.Single(SchoolCsvParser.ParseUtf8(bytes)).Name);
    }

    [Fact] public void SchoolStageMismatchesCannotBeSaved()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("مديرية اختبار", "2026/2027", "root", "Strong-Password-123!");
        var admin = db.Authenticate("root", "Strong-Password-123!")!;
        db.AddSchool(admin, "1", "مدرسة", "الغرب", "حكومية", "أساسي");
        var school = Assert.Single(db.GetSchools(admin));
        Assert.Throws<ArgumentException>(() => db.SaveDraft(admin, school.Id,
            new CensusData([new EnrollmentLine("11", 2, 3)], 1, 1, 1, "")));
    }
}
