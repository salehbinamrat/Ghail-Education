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
    public void GenuineEncryptedPhase1SchemaUpgradesWithoutDeletingSchoolOrStudent()
    {
        var path=Path.Combine(Path.GetTempPath(),"phase2-upgrade-"+Guid.NewGuid().ToString("N")+".db");
        var store=new SqliteStore(path);
        using (var con=DatabaseSecurity.OpenForRegressionTest(path))
        {
            con.Open();
            using var cmd=con.CreateCommand();
            cmd.CommandText="""
                CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE schools(
                    id TEXT PRIMARY KEY,code TEXT NOT NULL UNIQUE,name TEXT NOT NULL,
                    zone TEXT NOT NULL,ownership TEXT NOT NULL,stage TEXT NOT NULL,
                    is_active INTEGER NOT NULL DEFAULT 1,created_utc TEXT NOT NULL,
                    gender TEXT NOT NULL DEFAULT 'ذكور',is_kindergarten INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE users(
                    id TEXT PRIMARY KEY,username TEXT NOT NULL UNIQUE,password_hash TEXT NOT NULL,
                    role TEXT NOT NULL,school_id TEXT NULL,is_active INTEGER NOT NULL DEFAULT 1,
                    failed_attempts INTEGER NOT NULL DEFAULT 0,lock_until_utc TEXT NULL,
                    created_utc TEXT NOT NULL,permission_mask INTEGER NOT NULL DEFAULT 0,
                    is_owner INTEGER NOT NULL DEFAULT 0,auth_version INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE census_rounds(id TEXT PRIMARY KEY,academic_year TEXT UNIQUE,
                    snapshot_date TEXT NOT NULL,is_active INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE school_grades(
                    id TEXT PRIMARY KEY,school_id TEXT NOT NULL,academic_year TEXT NOT NULL,
                    grade INTEGER NOT NULL,section TEXT NOT NULL,created_utc TEXT NOT NULL,
                    UNIQUE(school_id,academic_year,grade,section),UNIQUE(id,school_id,academic_year));
                CREATE TABLE school_students(
                    id TEXT PRIMARY KEY,school_id TEXT NOT NULL,student_code TEXT NOT NULL,
                    full_name TEXT NOT NULL,gender TEXT NOT NULL,
                    guardian_name TEXT NOT NULL DEFAULT '',guardian_phone TEXT NOT NULL DEFAULT '',
                    notes TEXT NOT NULL DEFAULT '',is_active INTEGER NOT NULL DEFAULT 1,
                    created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,
                    UNIQUE(school_id,student_code),UNIQUE(id,school_id));
                CREATE TABLE student_enrollments(
                    student_id TEXT NOT NULL,school_id TEXT NOT NULL,academic_year TEXT NOT NULL,
                    grade_id TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'مسجل',
                    updated_utc TEXT NOT NULL,PRIMARY KEY(student_id,academic_year));
                INSERT INTO settings VALUES('academic_year','2026/2027');
                INSERT INTO schools(id,code,name,zone,ownership,stage,created_utc)
                    VALUES('school1','S01','مدرسة','العليب','حكومية','أساسي','2026-01-01');
                INSERT INTO census_rounds VALUES('round1','2026/2027','2026-09-01',1);
                INSERT INTO school_grades VALUES('grade1','school1','2026/2027',3,'أ','2026-09-01');
                INSERT INTO school_students(id,school_id,student_code,full_name,gender,created_utc,updated_utc)
                    VALUES('student1','school1','0001','سالم','ذكور','2026-09-01','2026-09-01');
                INSERT INTO student_enrollments VALUES('student1','school1','2026/2027','grade1','مسجل','2026-09-01');
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }
        store.Initialize();
        Assert.Equal(2,store.SchemaVersion);
        using var reopened=DatabaseSecurity.OpenForRegressionTest(path);
        reopened.Open();
        using var verify=reopened.CreateCommand();
        verify.CommandText="""
            SELECT COUNT(*) FROM student_person_links
            WHERE student_id='student1' AND person_id='student1'
            """;
        Assert.Equal(1,Convert.ToInt32(verify.ExecuteScalar()));
        verify.CommandText="SELECT COUNT(*) FROM student_enrollments WHERE student_id='student1'";
        Assert.Equal(1,Convert.ToInt32(verify.ExecuteScalar()));
        verify.CommandText="PRAGMA foreign_key_check;";
        using var foreign=verify.ExecuteReader();
        Assert.False(foreign.Read());
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
    public void ClosedYearBlocksAttendanceMutationsAndKeepsEarlierRecord()
    {
        var (store, owner, school, _) = Setup();
        store.AddSchoolGrade(owner, school.Id, 3, "أ");
        store.SaveStudent(owner, new SchoolStudent("", school.Id, "004", "طالب", 3, "أ", "", "", "", "", true, "2026/2027"));
        var student=store.GetStudents(owner, school.Id).Single();
        store.RecordStudentAttendance(owner, school.Id, student.Id, new DateOnly(2026, 11, 17), "حاضر", "");
        store.CloseAcademicYear(owner, "2026/2027");
        Assert.ThrowsAny<Exception>(() =>
            store.RecordStudentAttendance(owner, school.Id, student.Id, new DateOnly(2026, 11, 17), "غائب", ""));
        Assert.Equal("حاضر", store.GetStudentAttendance(owner, school.Id, student.Id).Single().Status);
    }

    [Fact]
    public void CrossSchoolTransferKeepsPreviousStudentRecordAndResults()
    {
        var (store, owner, school, _) = Setup();
        store.AddSchool(owner, "S02", "مدرسة ثانية", "المدينة", "حكومية", "أساسي", "ذكور");
        var other=store.GetSchools(owner).Single(x => x.Code=="S02");
        store.AddSchoolGrade(owner, school.Id, 3, "أ");
        store.AddSchoolGrade(owner, other.Id, 3, "ب");
        store.SaveStudent(owner, new SchoolStudent("", school.Id, "005", "طالب", 3, "أ", "", "", "", "", true, "2026/2027"));
        var student=store.GetStudents(owner, school.Id).Single();
        store.SaveStudentResult(owner, school.Id, student.Id, "الفصل الأول", "العلوم", 90, 100, "");
        store.TransitionStudent(owner, school.Id, student.Id, other.Id, 3, "ب", "school_transfer");
        Assert.Throws<InvalidOperationException>(() =>
            store.TransitionStudent(owner, school.Id, student.Id, other.Id, 3, "ب", "school_transfer"));
        Assert.Empty(store.GetStudents(owner, school.Id));
        Assert.Single(store.GetStudents(owner, other.Id));
        Assert.Equal(90, store.GetStudentResults(owner, school.Id, student.Id).Single().Score);
        Assert.Equal(2, store.GetStudentHistory(owner, school.Id, student.Id)
            .Select(x=>x.SchoolId).Distinct().Count());
    }

    [Fact]
    public void GraduationArchivesStudentWithoutDeletingGradesOrHistory()
    {
        var (store, owner, school, _) = Setup();
        store.AddSchoolGrade(owner, school.Id, 9, "أ");
        store.SaveStudent(owner, new SchoolStudent("", school.Id, "006", "طالب", 9, "أ", "", "", "", "", true, "2026/2027"));
        var student=store.GetStudents(owner, school.Id).Single();
        store.SaveStudentResult(owner, school.Id, student.Id, "الفصل الأول", "العلوم", 80, 100, "");
        store.CompleteStudentEnrollment(owner, school.Id, student.Id, "graduate");
        Assert.Empty(store.GetStudents(owner, school.Id));
        Assert.Equal(80, store.GetStudentResults(owner, school.Id, student.Id).Single().Score);
        Assert.Contains(store.GetStudentHistory(owner, school.Id, student.Id), x => x.Action=="graduate");
        Assert.Throws<InvalidOperationException>(() =>
            store.CompleteStudentEnrollment(owner, school.Id, student.Id, "graduate"));
    }

    [Fact]
    public void SchoolScopedAccountsCannotReadOrWriteAnotherSchoolsNewProfiles()
    {
        var (store, owner, school, _) = Setup();
        store.AddSchool(owner, "S02", "مدرسة ثانية", "المدينة", "حكومية", "أساسي", "ذكور");
        var other=store.GetSchools(owner).Single(x=>x.Code=="S02");
        store.CreateUser(owner, "school02", "Secure-School-Password-123!", Roles.Principal, school.Id);
        var principal=store.Authenticate("school02", "Secure-School-Password-123!")!;
        Assert.Throws<UnauthorizedAccessException>(() => store.GetSchoolInfrastructure(principal, other.Id));
        Assert.Throws<UnauthorizedAccessException>(() => store.GetSchoolFacilities(principal, other.Id));
        Assert.Throws<UnauthorizedAccessException>(() => store.SetSchoolFacility(principal, other.Id, "مختبر", 1, ""));
        Assert.Throws<UnauthorizedAccessException>(() => store.AddPhysicalClassroom(principal, other.Id, "R1", "", 20));
        Assert.Throws<UnauthorizedAccessException>(() => store.SaveSchoolInfrastructure(principal, other.Id,
            new SchoolInfrastructure("","","","",null,null,"","",null,"")));
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
