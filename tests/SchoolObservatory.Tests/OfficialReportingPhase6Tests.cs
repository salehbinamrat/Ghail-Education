using SchoolObservatory.Core;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class OfficialReportingPhase6Tests
{
    [Fact]
    public void ReportsMustNotPromoteOperationalDataToOfficialStatus()
    {
        var document = new EducationalReport("الطلاب", "مديرية غيل بن يمين", "2026/2027",
            false, ["المدرسة", "العدد"], [new ReportRecord(["مدرسة العليب", "12"])],
            new Dictionary<string, string>(), DateTimeOffset.UtcNow);
        Assert.False(document.IsOfficiallyApproved);
        Assert.Contains("غير معتمد", document.ApprovalLabel);
    }

    [Fact]
    public void EveryRowMustMatchTheDeclaredColumnCount()
    {
        Assert.Throws<ArgumentException>(() => new EducationalReport(
            "كشف", "مدرسة", "2026", false, ["أ", "ب"],
            [new ReportRecord(["واحد"])], new Dictionary<string,string>(), DateTimeOffset.UtcNow).Validate());
    }

    [Theory]
    [InlineData("=SUM(A1:A2)")]
    [InlineData(" +CMD")]
    [InlineData("@evil")]
    [InlineData("-1+2")]
    [InlineData("\t=HYPERLINK(\"url\")")]
    public void SpreadsheetTextNeverExecutesAsFormula(string input)
    {
        var escaped = ReportTextSafety.AsSpreadsheetText(input);
        Assert.StartsWith("'", escaped);
    }

    [Fact]
    public void ReportMustRespectSchoolScopeAndPersonalInformationPermissions()
    {
        var principal = new UserAccount("u", "principal", Roles.Principal, "school-a", true,
            (long)PermissionCatalog.DefaultForRole(Roles.Principal));
        Assert.False(ReportAccess.CanRead(principal, "school-b", PermissionFlags.ViewStudents));
        Assert.True(ReportAccess.CanRead(principal, "school-a", PermissionFlags.ViewStudents));
        var viewer = principal with { PermissionMask = (long)PermissionFlags.ViewSchools };
        Assert.False(ReportAccess.CanRead(viewer, "school-a", PermissionFlags.ViewStudents));
    }

    [Fact]
    public void TotalsUseTheExactVisibleRows()
    {
        var d = new EducationalReport("كشف", "مدرسة", "2026", false,
            ["الاسم"], [new ReportRecord(["محمد"]), new ReportRecord(["علي"])],
            new Dictionary<string,string>(), DateTimeOffset.UtcNow);
        Assert.Equal(2, d.RecordTotal);
    }
    [Fact]
    public void UnsupportedFilterDimensionsMustNeverBeSilentlyIgnored()
    {
        Assert.Throws<ArgumentException>(() => ReportFilterRules.Validate(
            new EducationalReportFilter(EducationalReportKind.ApprovedCensus, Grade: 3)));
        Assert.Throws<ArgumentException>(() => ReportFilterRules.Validate(
            new EducationalReportFilter(EducationalReportKind.StaffDistribution, Gender: "ذكور")));
        Assert.Throws<ArgumentException>(() => ReportFilterRules.Validate(
            new EducationalReportFilter(EducationalReportKind.StudentEnrollment, StaffCategory: "معلم")));
        ReportFilterRules.Validate(new EducationalReportFilter(
            EducationalReportKind.Attendance, Grade: 3, Section: "أ", Gender: "ذكور"));
    }

}
