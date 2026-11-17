using System;
using System.IO;
using System.Linq;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class AcademicYearPhase2Tests
{
    private static (SqliteStore Store, UserAccount Owner, School School, string Path) Setup()
    {
        var path = Path.Combine(Path.GetTempPath(), "phase2-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteStore(path);
        store.Initialize();
        store.InitializeAdministrator("غيل بن يمين", "2026/2027", "owner", "Secure-Owner-Password-123!");
        var owner = store.Authenticate("owner", "Secure-Owner-Password-123!")!;
        store.AddSchool(owner, "S01", "مدرسة اختبار", "العليب", "حكومية", "أساسي", "ذكور");
        return (store, owner, store.GetSchools(owner).Single(), path);
    }

    [Fact]
    public void UpgradeFromPhase1PreservesStudentsAndOfficialResults()
    {
        var (store, owner, school, path) = Setup();
        store.AddSchoolGrade(owner, school.Id, 3, "أ");
        store.SaveStudent(owner, new SchoolStudent("", school.Id, "001", "طالب", 3, "أ", "", "", "", "", true, "2026/2027"));
        var student = store.GetStudents(owner, school.Id).Single();
        store.SaveStudentResult(owner, school.Id, student.Id, "الفصل الأول", "رياضيات", 92, 100, "");
        store.Initialize();
        var reopened = new SqliteStore(path);
        reopened.Initialize();
        Assert.Equal(2, reopened.SchemaVersion);
        Assert.Equal(student.Id, reopened.GetStudents(owner, school.Id).Single().Id);
        Assert.Equal(92, reopened.GetStudentResults(owner, school.Id, student.Id).Single().Score);
        Assert.Single(reopened.GetAcademicYears(owner));
    }

    [Fact]
    public void YearClosureProtectsExistingGradesAndResultsAndKeepsHistory()
    {
        var (store, owner, school, _) = Setup();
        store.AddSchoolGrade(owner, school.Id, 3, "أ");
        store.SaveStudent(owner, new SchoolStudent("", school.Id, "002", "طالب", 3, "أ", "", "", "", "", true, "2026/2027"));
        var student = store.GetStudents(owner, school.Id).Single();
        store.CloseAcademicYear(owner, "2026/2027");
        Assert.ThrowsAny<Exception>(() => store.SaveStudentResult(owner, school.Id, student.Id, "الفصل الأول", "رياضيات", 90, 100, ""));
        Assert.ThrowsAny<Exception>(() => store.AddSchoolGrade(owner, school.Id, 4, "أ"));
        Assert.Equal("closed", store.GetAcademicYears(owner).Single().Status);
        store.OpenAcademicYear(owner, "2027/2028");
        store.SetActiveAcademicYear(owner, "2027/2028");
        Assert.Equal("2027/2028", store.AcademicYear);
        Assert.Equal("closed", store.GetAcademicYears(owner).Single(x => x.Year == "2026/2027").Status);
    }

    [Fact]
    public void StudentPromotionIsIdempotentAndDoesNotRewritePriorEnrollment()
    {
        var (store, owner, school, _) = Setup();
        store.AddSchoolGrade(owner, school.Id, 3, "أ");
        store.SaveStudent(owner, new SchoolStudent("", school.Id, "003", "طالب", 3, "أ", "", "", "", "", true, "2026/2027"));
        var student = store.GetStudents(owner, school.Id).Single();
        store.OpenAcademicYear(owner, "2027/2028");
        store.SetActiveAcademicYear(owner, "2027/2028");
        store.AddSchoolGrade(owner, school.Id, 4, "أ");
        store.TransitionStudent(owner, school.Id, student.Id, school.Id, 4, "أ", "promote");
        Assert.Throws<InvalidOperationException>(() => store.TransitionStudent(owner, school.Id, student.Id, school.Id, 4, "أ", "promote"));
        Assert.Equal(4, store.GetStudents(owner, school.Id).Single().Grade);
        Assert.Contains(store.GetStudentHistory(owner, school.Id, student.Id), x => x.AcademicYear == "2026/2027" && x.Grade == 3);
    }

    [Fact]
    public void PhysicalRoomIsNotTheSameAsSectionAndCanBeReassigned()
    {
        var (store, owner, school, _) = Setup();
        var a = store.AddSchoolGrade(owner, school.Id, 3, "أ");
        var b = store.AddSchoolGrade(owner, school.Id, 3, "ب");
        Assert.NotEqual(a.Id, b.Id);
        var room = store.AddPhysicalClassroom(owner, school.Id, "R01", "المبنى أ", 35);
        store.AssignSectionRoom(owner, school.Id, a.Id, room);
        store.AssignSectionRoom(owner, school.Id, b.Id, room);
        Assert.Equal(2, store.GetSchoolSectionsWithRooms(owner, school.Id).Count);
    }

    [Fact]
    public void EmployeeAssignmentKeepsOneIdentityAndChronologicalWorkplaceHistory()
    {
        var (store, owner, school, _) = Setup();
        store.AddSchool(owner, "S02", "مدرسة ثانية", "المدينة", "حكومية", "أساسي", "ذكور");
        var other = store.GetSchools(owner).Single(x => x.Code == "S02");
        store.SaveSchoolStaff(owner, new SchoolStaff("", school.Id, "EMP01", "معلم", "معلم", "مدرس", "", "", "ذكور", true));
        var staff = store.GetSchoolStaff(owner, school.Id).Single();
        store.AssignEmployee(owner, school.Id, staff.Id, other.Id, "secondment", new DateOnly(2026, 9, 1), null, "انتداب");
        Assert.Single(store.GetSchoolStaff(owner, school.Id));
        Assert.Single(store.GetEmployeeAssignments(owner, school.Id, staff.Id));
        Assert.Equal(other.Id, store.GetEmployeeAssignments(owner, school.Id, staff.Id)[0].WorkplaceSchoolId);
        Assert.Throws<InvalidOperationException>(() => store.AssignEmployee(owner, school.Id, staff.Id, other.Id, "secondment", new DateOnly(2026, 10, 1), null, ""));
    }

    [Fact]
    public void SchoolProfileFacilitiesAreSchoolScoped()
    {
        var (store, owner, school, _) = Setup();
        store.SaveSchoolInfrastructure(owner, school.Id,
            new SchoolInfrastructure("غيل بن يمين", "العليب", "القرية", "شارع المدرسة",
                null, null, "حكومي", "جيد", 12, "صباحي"));
        store.SetSchoolFacility(owner, school.Id, "مختبر", 1, "قائم");
        Assert.Equal(12, store.GetSchoolInfrastructure(owner, school.Id).ClassroomsCount);
        Assert.Single(store.GetSchoolFacilities(owner, school.Id));
    }

    [Fact]
    public void ScopedSchoolAccountCannotControlAcademicYears()
    {
        var (store, owner, school, _) = Setup();
        store.CreateUser(owner, "school01", "Secure-School-Password-123!", Roles.Principal, school.Id);
        var principal = store.Authenticate("school01", "Secure-School-Password-123!")!;
        Assert.Throws<UnauthorizedAccessException>(() => store.OpenAcademicYear(principal, "2027/2028"));
        Assert.Throws<UnauthorizedAccessException>(() => store.CloseAcademicYear(principal, "2026/2027"));
    }
}
