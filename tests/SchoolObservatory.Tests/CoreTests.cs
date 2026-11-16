using SchoolObservatory.Core;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class CoreTests
{
    [Fact] public void HashIsSaltedAndVerifiable()
    {
        const string password = "Strong-Password-123!";
        var a = PasswordService.Hash(password);
        var b = PasswordService.Hash(password);
        Assert.NotEqual(a, b);
        Assert.True(PasswordService.Verify(password, a));
        Assert.False(PasswordService.Verify("bad", a));
    }

    [Fact] public void InvalidCensusIsRejected()
    {
        var errors = CensusValidation.Validate(new CensusData(
            [new EnrollmentLine("1", -2, 4), new EnrollmentLine("1", 2, 3)], 0, 0, -1, ""));
        Assert.True(errors.Count >= 2);
    }

    [Fact] public void SchoolEntryCannotApproveDistrictData()
    {
        var entry = new UserAccount("u", "data", Roles.Entry, "a", true);
        Assert.False(AccessPolicy.CanPerform(entry, "district_approve", "a", Statuses.SchoolApproved));
        Assert.False(AccessPolicy.CanEditCensus(entry, "b"));
        Assert.True(AccessPolicy.CanEditCensus(entry, "a"));
    }

    [Fact] public void ApprovalRequiresSchoolReview()
    {
        var district = new UserAccount("u", "admin", Roles.Statistician, null, true);
        Assert.False(AccessPolicy.CanPerform(district, "district_approve", "s", Statuses.Submitted));
        Assert.True(AccessPolicy.CanPerform(district, "district_approve", "s", Statuses.SchoolApproved));
    }
}
