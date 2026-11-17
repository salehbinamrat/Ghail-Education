using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

/// <summary>Phase 2 red/green acceptance tests: no data loss, no double advancement, strict school scope.</summary>
public sealed class AcademicHistoryPhase2Tests
{
    private static (SqliteStore Db, UserAccount Owner, School A, School B, string Path) Prepare()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "phase2-" +
            Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Secure-Founder-Password-123!");
        var owner = Assert.IsType<UserAccount>(db.Authenticate("founder", "Secure-Founder-Password-123!"));
        db.AddSchool(owner, "A01", "مدرسة أ", "العليب", "حكومية", "أساسي", "ذكور");
        db.AddSchool(owner, "B01", "مدرسة ب", "العليب", "حكومية", "أساسي", "ذكور");
        var schools = db.GetSchools(owner);
        return (db, owner, schools.Single(s => s.Code == "A01"),
            schools.Single(s => s.Code == "B01"), path);
    }

    private static string AddStudent(SqliteStore db, UserAccount owner, School school)
    {
        db.AddSchoolGrade(owner, school.Id, 3, "أ");
        db.SaveStudent(owner, new SchoolStudent("", school.Id, "S001", "طالب أصيل",
            3, "أ", "", "ولي الأمر", "777000000", "", true, "2026/2027"));
        return Assert.Single(db.GetStudents(owner, school.Id)).Id;
    }

    [Fact]
    public void YearsHaveOneActiveYearAndNeverEraseHistory()
    {
        var (db, owner, a, _, _) = Prepare();
        var id = AddStudent(db, owner, a);
        db.SaveStudentResult(owner, a.Id, id, "الفصل الأول", "الرياضيات", 90, 100, "");
        db.RecordStudentAttendance(owner, a.Id, id, new DateOnly(2026, 11, 17), "حاضر", "");
        db.OpenAcademicYear(owner, "2027/2028");
        Assert.Equal("2026/2027", db.AcademicYear);
        db.ActivateAcademicYear(owner, "2027/2028");
        Assert.Equal("2027/2028", db.AcademicYear);
        db.CloseAcademicYear(owner, "2026/2027");
        var years = db.GetAcademicYears(owner);
        Assert.Equal(2, years.Count);
        Assert.Single(years, y => y.IsActive);
        Assert.True(years.Single(y => y.Year == "2026/2027").IsClosed);
        Assert.Single(db.GetStudentResults(owner, a.Id, id));
        Assert.Single(db.GetStudentAttendance(owner, a.Id, id));
    }

    [Fact]
    public void ClosedYearCannotBeReactivatedOrUpdatedAndCannotBeClosedWhileActive()
    {
        var (db, owner, a, _, _) = Prepare();
        var id = AddStudent(db, owner, a);
        Assert.Throws<InvalidOperationException>(() => db.CloseAcademicYear(owner, "2026/2027"));
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        db.CloseAcademicYear(owner, "2026/2027");
        Assert.Throws<InvalidOperationException>(() => db.ActivateAcademicYear(owner, "2026/2027"));
        Assert.Throws<InvalidOperationException>(() => db.CloseAcademicYear(owner, "2026/2027"));
        Assert.Single(db.GetStudentEnrollmentHistory(owner, id));
    }

    [Fact]
    public void PromotionAndRepeatingNeverDuplicateActiveYearEnrollment()
    {
        var (db, owner, a, _, _) = Prepare();
        var id = AddStudent(db, owner, a);
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        db.AddSchoolGrade(owner, a.Id, 4, "أ");
        db.AddSchoolGrade(owner, a.Id, 4, "ب");
        db.PromoteStudent(owner, id, a.Id, 4, "أ", repeat: false);
        Assert.Throws<InvalidOperationException>(() =>
            db.PromoteStudent(owner, id, a.Id, 4, "أ", repeat: false));
        Assert.Equal(2, db.GetStudentEnrollmentHistory(owner, id).Count);
        Assert.Equal(4, db.GetStudents(owner, a.Id).Single().Grade);
        db.TransferStudentSection(owner, id, a.Id, 4, "ب");
        Assert.Equal("ب", db.GetStudents(owner, a.Id).Single().Section);
        Assert.Equal(3, db.GetStudentEnrollmentHistory(owner, id).Count);
    }

    [Fact]
    public void InterSchoolTransferKeepsStableStudentIdAndPreviousSchoolResults()
    {
        var (db, owner, a, b, _) = Prepare();
        var id = AddStudent(db, owner, a);
        db.SaveStudentResult(owner, a.Id, id, "الفصل الأول", "العربية", 93, 100, "");
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        db.AddSchoolGrade(owner, b.Id, 4, "أ");
        db.TransferStudentSchool(owner, id, b.Id, 4, "أ");
        Assert.Empty(db.GetStudents(owner, a.Id));
        Assert.Equal(id, Assert.Single(db.GetStudents(owner, b.Id)).Id);
        Assert.Single(db.GetStudentResults(owner, a.Id, id));
        var history = db.GetStudentEnrollmentHistory(owner, id);
        Assert.Equal(2, history.Count);
        Assert.Equal(a.Id, history.First().SchoolId);
        Assert.Equal(b.Id, history.Last().SchoolId);
    }

    [Fact]
    public void SectionsPhysicalRoomsAndShiftsAreDifferentDimensions()
    {
        var (db, owner, a, _, _) = Prepare();
        var first = db.AddSchoolGrade(owner, a.Id, 3, "أ");
        var second = db.AddSchoolGrade(owner, a.Id, 3, "ب");
        Assert.NotEqual(first.Id, second.Id);
        var room = db.AddPhysicalClassroom(owner, a.Id, "غرفة 1", 35);
        db.AssignSectionRoom(owner, first.Id, room.Id, "صباحي");
        db.AssignSectionRoom(owner, second.Id, room.Id, "مسائي");
        Assert.Equal(2, db.GetSectionRoomAssignments(owner, a.Id, "2026/2027").Count);
        Assert.Single(db.GetPhysicalClassrooms(owner, a.Id));
    }

    [Fact]
    public void EmployeeIdentityIsStableAcrossAssignmentsAndPayrollSchoolNeverMoves()
    {
        var (db, owner, a, b, _) = Prepare();
        db.SaveSchoolStaff(owner, new SchoolStaff("", a.Id, "E0042", "معلم",
            "معلم", "معلم", "الرياضيات", "بكالوريوس", "ذكور", true));
        var old = Assert.Single(db.GetSchoolStaff(owner, a.Id));
        db.AssignEmployee(owner, old.Id, b.Id, "انتداب",
            new DateOnly(2026, 11, 17), null);
        var history = db.GetEmployeeAssignments(owner, old.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal(a.Id, history[0].PayrollSchoolId);
        Assert.Equal(a.Id, history[1].PayrollSchoolId);
        Assert.Equal(b.Id, history[1].WorkplaceSchoolId);
        Assert.Equal(old.Id, Assert.Single(db.GetSchoolStaff(owner, a.Id)).Id);
        Assert.Empty(db.GetSchoolStaff(owner, b.Id));
    }

    [Fact]
    public void AScopedSchoolOperatorCannotSeeOrTransferAnotherSchoolsRecords()
    {
        var (db, owner, a, b, _) = Prepare();
        var id = AddStudent(db, owner, a);
        db.AddSchoolGrade(owner, b.Id, 3, "أ");
        db.CreateUser(owner, "schooluser", "School-user-safe-654321", Roles.Entry, a.Id);
        var actor = Assert.IsType<UserAccount>(db.Authenticate("schooluser", "School-user-safe-654321"));
        Assert.Throws<UnauthorizedAccessException>(() => db.TransferStudentSchool(actor, id, b.Id, 3, "أ"));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetPhysicalClassrooms(actor, b.Id));
        Assert.Throws<UnauthorizedAccessException>(() => db.OpenAcademicYear(actor, "2027/2028"));
        Assert.Single(db.GetStudents(owner, a.Id));
    }


    [Fact]
    public void EditedStudentSectionAndLegacyImportMustSynchronizeCurrentPlacement()
    {
        var (db, owner, a, _, _) = Prepare();
        var studentId = AddStudent(db, owner, a);
        db.AddSchoolGrade(owner, a.Id, 3, "ب");
        var old = db.GetStudent(owner, a.Id, studentId);
        db.SaveStudent(owner, old with { Section = "ب" });
        Assert.Equal("ب", db.GetStudent(owner, a.Id, studentId).Section);
        Assert.Equal("ب", db.GetStudentEnrollmentHistory(owner, studentId).Single().Section);
    }

    [Fact]
    public void TransferredStudentCanRecordNewSchoolResultsWithoutOverwritingEarlierResults()
    {
        var (db, owner, a, b, _) = Prepare();
        var id = AddStudent(db, owner, a);
        db.SaveStudentResult(owner, a.Id, id, "الفصل الأول", "العربية", 80, 100, "");
        db.RecordStudentAttendance(owner, a.Id, id, new DateOnly(2026, 11, 17), "حاضر", "");
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        db.AddSchoolGrade(owner, b.Id, 4, "أ");
        db.TransferStudentSchool(owner, id, b.Id, 4, "أ");
        db.SaveStudentResult(owner, b.Id, id, "الفصل الأول", "العربية", 94, 100, "");
        db.RecordStudentAttendance(owner, b.Id, id, new DateOnly(2027, 11, 17), "غائب", "بعذر");
        Assert.Equal(80, Assert.Single(db.GetStudentResults(owner, a.Id, id)).Score);
        Assert.Equal(94, Assert.Single(db.GetStudentResults(owner, b.Id, id)).Score);
        Assert.Single(db.GetStudentAttendance(owner, a.Id, id));
        Assert.Single(db.GetStudentAttendance(owner, b.Id, id));
    }

    [Fact]
    public void StudentGraduationAndWithdrawalArchivePlacementsNotPersonIdentity()
    {
        var (db, owner, a, _, _) = Prepare();
        var id = AddStudent(db, owner, a);
        db.WithdrawStudent(owner, id);
        Assert.Empty(db.GetStudents(owner, a.Id));
        Assert.Equal("منسحب", Assert.Single(db.GetStudentEnrollmentHistory(owner, id)).Status);
        Assert.Throws<InvalidOperationException>(() => db.GraduateStudent(owner, id));
    }

    [Fact]
    public void SchoolInfrastructureAndFacilitiesAreScopedAndPreservedAfterReopen()
    {
        var (db, owner, a, b, path) = Prepare();
        var profile = new SchoolInfrastructureRecord(a.Id, "غيل بن يمين", "العليب",
            "مبنى مستقل", "حكومي", 12, "صباحي ومسائي", true, true, false, "بحاجة إلى مختبر");
        db.SaveSchoolInfrastructure(owner, profile);
        db.SaveSchoolFacility(owner, new SchoolFacility(a.Id, "المكتبة", 1, "جيدة"));
        var reopened = new SqliteStore(path);
        reopened.Initialize();
        Assert.Equal(profile, reopened.GetSchoolInfrastructure(owner, a.Id));
        Assert.Single(reopened.GetSchoolFacilities(owner, a.Id));
        Assert.Empty(reopened.GetSchoolFacilities(owner, b.Id));
        db.CreateUser(owner, "scoped-a", "School-user-safe-654321", Roles.Entry, a.Id);
        var schoolUser = Assert.IsType<UserAccount>(db.Authenticate("scoped-a", "School-user-safe-654321"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            db.GetSchoolInfrastructure(schoolUser, b.Id));
    }

    [Fact]
    public void ClosedYearTriggersProtectOfficialResultsEvenOutsideTheBusinessMethods()
    {
        var (db, owner, a, _, path) = Prepare();
        var id = AddStudent(db, owner, a);
        db.SaveStudentResult(owner, a.Id, id, "الفصل الأول", "العربية", 70, 100, "");
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        db.CloseAcademicYear(owner, "2026/2027");
        using var con = DatabaseSecurity.OpenForRegressionTest(path);
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE student_results SET score=99 WHERE student_id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(70, Assert.Single(db.GetStudentResults(owner, a.Id, id)).Score);
    }


    [Fact]
    public void ClosedYearRostersStayQueryableAndSchoolScopedWithoutReactivatingThem()
    {
        var (db, owner, a, b, _) = Prepare();
        var id = AddStudent(db, owner, a);
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        db.CloseAcademicYear(owner, "2026/2027");
        Assert.Empty(db.GetStudents(owner, a.Id));
        var old = Assert.Single(db.GetHistoricalStudents(owner, a.Id, "2026/2027"));
        Assert.Equal(id, old.Id);
        Assert.Equal("2026/2027", old.AcademicYear);
        Assert.Empty(db.GetHistoricalStudents(owner, b.Id, "2026/2027"));
        db.CreateUser(owner, "archive-b", "School-user-safe-654321", Roles.Entry, b.Id);
        var restricted = Assert.IsType<UserAccount>(db.Authenticate("archive-b", "School-user-safe-654321"));
        Assert.Throws<UnauthorizedAccessException>(() => db.GetHistoricalStudents(restricted, a.Id, "2026/2027"));
    }


    [Fact]
    public void PersonnelEditSynchronizesNormalizedCareerWithoutCloningEmployee()
    {
        var (db, owner, a, _, _) = Prepare();
        db.SaveSchoolStaff(owner, new SchoolStaff("", a.Id, "E66", "أحمد",
            "معلم", "مدرس", "الرياضيات", "بكالوريوس", "ذكور", true));
        var staffId = Assert.Single(db.GetSchoolStaff(owner, a.Id)).Id;
        db.SaveStaffPersonnel(owner, a.Id, staffId,
            new StaffPersonnelDetails("موظف", 2013, null, 2010,
                "مباشر", "", null, "", ""));
        var career = db.GetEmployeeCareer(owner, staffId);
        Assert.Equal(2010, career.QualificationYear);
        Assert.Equal(2013, career.EmploymentYear);
        Assert.Equal("موظف", career.EmploymentCategory);
        Assert.Equal(a.Id, career.OriginalPayrollSchoolId);
    }


    [Fact]
    public void TransferToSchoolWithSameLocalCodeAssignsUniqueDestinationCodeWithoutCloningStudent()
    {
        var (db, owner, a, b, _) = Prepare();
        var id = AddStudent(db, owner, a);
        db.AddSchoolGrade(owner, b.Id, 3, "أ");
        db.SaveStudent(owner, new SchoolStudent("", b.Id, "S001", "طالب آخر",
            3, "أ", "", "", "", "", true, "2026/2027"));
        var other = Assert.Single(db.GetStudents(owner, b.Id));
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        db.AddSchoolGrade(owner, b.Id, 3, "أ");
        db.AddSchoolGrade(owner, b.Id, 4, "أ");
        db.PromoteStudent(owner, other.Id, b.Id, 3, "أ", repeat: true);
        db.TransferStudentSchool(owner, id, b.Id, 4, "أ");
        var students = db.GetStudents(owner, b.Id);
        Assert.Equal(2, students.Count);
        Assert.Equal(2, students.Select(x => x.StudentCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(id, students.Single(x => x.Id == id).Id);
        Assert.Equal("S001", db.GetHistoricalStudents(owner, a.Id, "2026/2027").Single().StudentCode);
    }

    [Fact]
    public void AcademicStageSnapshotIsNotOverwrittenByLaterSchoolReclassification()
    {
        var (db, owner, a, _, _) = Prepare();
        db.AddSchoolGrade(owner, a.Id, 3, "أ");
        Assert.Equal("أساسي", Assert.Single(db.GetSchoolGrades(owner, a.Id)).Stage);
        db.UpdateSchool(owner, a with { Stage = "مجمع تربوي" });
        Assert.Equal("أساسي", Assert.Single(db.GetSchoolGrades(owner, a.Id)).Stage);
    }


    [Fact]
    public void ClosedYearAlsoProtectsCensusEnrollmentRowsAndDecisions()
    {
        var (db, owner, a, _, path) = Prepare();
        var census = new CensusData(new[] { new EnrollmentLine("3", 10, 0) },
            2, 0, 1, "");
        db.SaveDraft(owner, a.Id, census);
        db.OpenAcademicYear(owner, "2027/2028");
        db.ActivateAcademicYear(owner, "2027/2028");
        db.CloseAcademicYear(owner, "2026/2027");
        using var con = DatabaseSecurity.OpenForRegressionTest(path);
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "UPDATE enrollment_rows SET boys=999 WHERE grade='3'";
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQuery());
        }
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM enrollment_rows WHERE grade='3'";
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQuery());
        }
        using var verify = con.CreateCommand();
        verify.CommandText = "SELECT boys FROM enrollment_rows WHERE grade='3'";
        Assert.Equal(10L, Convert.ToInt64(verify.ExecuteScalar()));
    }

    [Fact]
    public void MigrationKeepsExistingStudentStaffAndAuditWithoutChangingIds()
    {
        var (db, owner, a, _, path) = Prepare();
        var id = AddStudent(db, owner, a);
        db.SaveSchoolStaff(owner, new SchoolStaff("", a.Id, "M02", "أحمد", "معلم",
            "معلم", "العربية", "دبلوم", "ذكور", true));
        var staffId = Assert.Single(db.GetSchoolStaff(owner, a.Id)).Id;
        var reopened = new SqliteStore(path);
        reopened.Initialize();
        Assert.Equal(id, Assert.Single(reopened.GetStudents(owner, a.Id)).Id);
        Assert.Equal(staffId, Assert.Single(reopened.GetSchoolStaff(owner, a.Id)).Id);
        Assert.Single(reopened.GetStudentEnrollmentHistory(owner, id));
        Assert.Single(reopened.GetEmployeeAssignments(owner, staffId));
        Assert.Contains(reopened.GetRecentAudit(owner), e => e.Action == "student.create");
    }
}
