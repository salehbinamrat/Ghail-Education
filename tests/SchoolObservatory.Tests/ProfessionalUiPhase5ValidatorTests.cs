using SchoolObservatory.Core;
using Xunit;

namespace SchoolObservatory.Tests;

/// <summary>Pure UI validation tests run without WPF or any database write.</summary>
public sealed class ProfessionalUiPhase5ValidatorTests
{
    [Theory]
    [InlineData("", "المدرسة", "القرية", "حكومية", "أساسي", "مختلطة", "code")]
    [InlineData("A01", "", "القرية", "حكومية", "أساسي", "مختلطة", "name")]
    [InlineData("A01", "المدرسة", "", "حكومية", "أساسي", "مختلطة", "zone")]
    [InlineData("A01", "المدرسة", "القرية", "أخرى", "أساسي", "مختلطة", "ownership")]
    [InlineData("A01", "المدرسة", "القرية", "حكومية", "غير معروف", "مختلطة", "stage")]
    [InlineData("A01", "المدرسة", "القرية", "حكومية", "أساسي", "أخرى", "gender")]
    public void SchoolEditorRejectsBadFieldsBeforeCallingStore(string code,string name,
        string zone,string ownership,string stage,string gender,string field)
    {
        var issues = SchoolFormValidator.Validate(code,name,zone,ownership,stage,gender);
        Assert.Contains(issues, issue => issue.Field == field);
        Assert.All(issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Message)));
    }

    [Fact]
    public void ValidSchoolHasNoUiErrors()
    {
        Assert.Empty(SchoolFormValidator.Validate("A001","مدرسة العليب","العليب",
            "حكومية","أساسي","ذكور"));
    }

    [Fact]
    public void LongNameAndCodeHaveSeparateFieldMessages()
    {
        var issues = SchoolFormValidator.Validate(new string('A',33),
            new string('ب',181),"منطقة","حكومية","أساسي","ذكور");
        Assert.Contains(issues,x=>x.Field=="code");
        Assert.Contains(issues,x=>x.Field=="name");
        Assert.DoesNotContain(issues,x=>x.Field=="zone");
    }
}
