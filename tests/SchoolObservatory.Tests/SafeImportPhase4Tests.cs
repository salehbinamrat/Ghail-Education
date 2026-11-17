using System.IO.Compression;
using SchoolObservatory.Core;
using SchoolObservatory.Data;
using Xunit;

namespace SchoolObservatory.Tests;

public sealed class SafeImportPhase4Tests
{
    private static (SqliteStore Db, UserAccount Owner, School School) New()
    {
        var path = Path.Combine(Path.GetTempPath(), "phase4-" + Guid.NewGuid().ToString("N") + ".db");
        var db = new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين", "2026/2027", "founder", "Strong-Founder-Password-123!");
        var actor = db.Authenticate("founder", "Strong-Founder-Password-123!")!;
        db.AddSchool(actor, "A01", "مدرسة العليب", "العليب", "حكومية", "أساسي", "ذكور");
        return (db, actor, Assert.Single(db.GetSchools(actor)));
    }

    private static ImportTable Table(params string[][] rows) =>
        new("csv", rows[0], rows.Skip(1).Select(x => (IReadOnlyList<string>)x).ToArray());

    private static ImportPreview Plan(SqliteStore db, UserAccount actor, ImportEntity type,
        string filename, ImportTable table) => db.PreviewImport(actor, type, filename, table,
            ImportColumnMapping.Auto(type, table.Headers));

    [Fact]
    public void SectionsAreImportedAsRealAcademicGroupsAndCannotBeReversedAfterStudentsJoin()
    {
        var (db, actor, school) = New();
        var sheet = Table(["school_code","grade","section"],
            ["A01","1","أ"],["A01","1","ب"]);
        var preview = Plan(db, actor, ImportEntity.Sections, "sections.csv", sheet);
        Assert.Equal(2, preview.Created);
        var batch = db.ApplyImport(actor, preview);
        Assert.Equal(2, db.GetSchoolGrades(actor, school.Id).Count);
        var repeat = Plan(db, actor, ImportEntity.Sections, "sections.csv", sheet);
        Assert.Equal(2, repeat.Unchanged);
        db.SaveStudent(actor, new SchoolStudent("",school.Id,"0001","طالب",
            1,"أ","","","","",true,"2026/2027"));
        Assert.Throws<InvalidOperationException>(() => db.ReverseImport(actor,batch.Id));
        Assert.Equal(2,db.GetSchoolGrades(actor,school.Id).Count);
    }

    [Fact]
    public void StudentImportPreservesOriginalRecordIdAndResultsAcrossRepeatedImports()
    {
        var (db, actor, school) = New();
        db.AddSchoolGrade(actor, school.Id, 1, "أ");
        var sheet = Table(["school_code","student_code","full_name","grade","section","gender"],
            ["A01","001","سالم","1","أ","ذكور"]);
        var batch = db.ApplyImport(actor,Plan(db,actor,ImportEntity.Students,"students.csv",sheet));
        Assert.Equal(1,batch.Created);
        var student=Assert.Single(db.GetStudents(actor,school.Id));
        db.SaveStudentResult(actor,school.Id,student.Id,"الفصل الأول","عربي",80,100,"");
        var unchanged=Plan(db,actor,ImportEntity.Students,"students.csv",sheet);
        Assert.Equal(ImportRowStatus.Unchanged,Assert.Single(unchanged.Rows).Status);
        db.ApplyImport(actor,unchanged);
        Assert.Equal(student.Id,Assert.Single(db.GetStudents(actor,school.Id)).Id);
        Assert.Single(db.GetStudentResults(actor,school.Id,student.Id));
        Assert.Throws<InvalidOperationException>(() => db.ReverseImport(actor,batch.Id));
    }

    [Fact]
    public void StaffImportKeepsIdentityAndDoesNotDuplicatePayrollAssignment()
    {
        var (db, actor, school) = New();
        var original=Table(["school_code","employee_code","full_name","category"],
            ["A01","E101","المعلم","معلم"]);
        db.ApplyImport(actor,Plan(db,actor,ImportEntity.Staff,"employees.csv",original));
        var employee=Assert.Single(db.GetSchoolStaff(actor,school.Id));
        var changed=Table(["school_code","employee_code","full_name","category"],
            ["A01","E101","المعلم بعد التصحيح",""]);
        var plan=Plan(db,actor,ImportEntity.Staff,"employees.csv",changed);
        Assert.Equal(ImportRowStatus.Update,Assert.Single(plan.Rows).Status);
        db.ApplyImport(actor,plan);
        Assert.Equal(employee.Id,Assert.Single(db.GetSchoolStaff(actor,school.Id)).Id);
        Assert.Single(db.GetEmployeeAssignments(actor,employee.Id));
    }

    [Fact]
    public void PhysicalDeletionRequiresArchivedEmptySchoolAndExactConfirmationCode()
    {
        var (db,actor,school)=New();
        Assert.Throws<InvalidOperationException>(() =>
            db.DeleteEmptySchool(actor,school.Id,school.Code));
        db.AddSchoolGrade(actor,school.Id,1,"أ");
        db.UpdateSchool(actor,school with {IsActive=false});
        Assert.Throws<InvalidOperationException>(() =>
            db.DeleteEmptySchool(actor,school.Id,school.Code));
        Assert.Single(db.GetSchools(actor));

        db.AddSchool(actor,"EMPTY","مدرسة فارغة","القرية","حكومية","أساسي","ذكور");
        var unused=db.GetSchools(actor).Single(x=>x.Code=="EMPTY");
        db.UpdateSchool(actor,unused with {IsActive=false});
        Assert.Throws<ArgumentException>(() => db.DeleteEmptySchool(actor,unused.Id,"WRONG"));
        db.DeleteEmptySchool(actor,unused.Id,unused.Code);
        Assert.DoesNotContain(db.GetSchools(actor),x=>x.Id==unused.Id);
    }

    [Fact]
    public void RejectsEmbeddedMacrosAndSuspiciousHighlyCompressedOfficePayloads()
    {
        var path=Path.Combine(Path.GetTempPath(),"phase4-office-"+Guid.NewGuid()+".xlsx");
        try
        {
            using(var archive=ZipFile.Open(path,ZipArchiveMode.Create))
            {
                using(var output=archive.CreateEntry("xl/worksheets/sheet1.xml").Open())
                using(var writer=new StreamWriter(output))
                    writer.Write("<worksheet/>");
                using(var payload=archive.CreateEntry("xl/vbaProject.bin").Open())
                    payload.WriteByte(1);
            }
            Assert.Throws<ArgumentException>(()=>ImportTableReader.Read(path));
            File.Delete(path);
            using(var archive=ZipFile.Open(path,ZipArchiveMode.Create))
            using(var output=archive.CreateEntry("xl/worksheets/sheet1.xml").Open())
            {
                var bytes=new byte[2_000_000];
                Array.Fill(bytes,(byte)'A');
                output.Write(bytes);
            }
            Assert.Throws<ArgumentException>(()=>ImportTableReader.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FormulaLikeTextAndInvalidHeaderAreRejectedBeforeCommit()
    {
        var (db,actor,_) = New();
        var dangerous = Table(["code","name","zone","ownership","stage"],
            ["NEW1","=2+3","الشرق","حكومية","أساسي"]);
        var preview=Plan(db,actor,ImportEntity.Schools,"test.csv",dangerous);
        var row=Assert.Single(preview.Rows);
        Assert.Equal(ImportRowStatus.Invalid,row.Status);
        Assert.Contains("الصف 2",row.Message);
        Assert.Contains("name",row.Message);
        Assert.Throws<InvalidOperationException>(()=>db.ApplyImport(actor,preview));
        Assert.Throws<ArgumentException>(()=>ImportColumnMapping.Auto(ImportEntity.Schools,
            ["missing","columns"]));
    }

    [Fact]
    public void MoreThanFiveThousandRowsCannotBePreviewed()
    {
        var (db,actor,_) = New();
        var rows=Enumerable.Range(0,5001)
            .Select(i=>(IReadOnlyList<string>)new[]{i.ToString(),"اسم","منطقة","حكومية","أساسي"}).ToArray();
        var sheet=new ImportTable("csv",["code","name","zone","ownership","stage"],rows);
        Assert.Throws<ArgumentException>(()=>Plan(db,actor,ImportEntity.Schools,"large.csv",sheet));
        Assert.Empty(db.GetImportBatches(actor));
    }

    [Fact]
    public void SchoolScopedStaffImportCannotSeeOrModifyAnotherSchool()
    {
        var (db,owner,school)=New();
        db.AddSchool(owner,"B01","مدرسة ثانية","منطقة","حكومية","أساسي","ذكور");
        db.CreateUser(owner,"staffphase4","Strong-Password-123!",Roles.Entry,school.Id,
            (long)(PermissionFlags.ViewSchools|PermissionFlags.ViewStaff|PermissionFlags.ManageStaff));
        var actor=db.Authenticate("staffphase4","Strong-Password-123!")!;
        var records=Table(["school_code","employee_code","full_name","category"],
            ["B01","E009","موظف","معلم"]);
        Assert.Throws<UnauthorizedAccessException>(()=>
            Plan(db,actor,ImportEntity.Staff,"foreign.csv",records));
        Assert.Empty(db.GetSchoolStaff(owner,school.Id));
    }

    [Fact]
    public void ReinitializationIsIdempotentAndKeepsTheImportLedger()
    {
        var (db,actor,school)=New();
        var sheet=Table(["code","name"],["A01","اسم محدث"]);
        var batch=db.ApplyImport(actor,Plan(db,actor,ImportEntity.Schools,"history.csv",sheet));
        db.Initialize();
        Assert.Equal(batch.Id,Assert.Single(db.GetImportBatches(actor)).Id);
        Assert.Equal(school.Id,Assert.Single(db.GetSchools(actor)).Id);
    }

    [Fact]
    public void EncryptedVersionFourUpgradePreservesStudentIdentityAndHistoricalResults()
    {
        var path=Path.Combine(Path.GetTempPath(),"phase4-upgrade-"+Guid.NewGuid()+".db");
        var db=new SqliteStore(path);
        db.Initialize();
        db.InitializeAdministrator("غيل بن يمين","2026/2027","founder","Strong-Founder-Password-123!");
        var actor=db.Authenticate("founder","Strong-Founder-Password-123!")!;
        db.AddSchool(actor,"MIG1","مدرسة اختبار","القرية","حكومية","أساسي","ذكور");
        var school=Assert.Single(db.GetSchools(actor));
        db.AddSchoolGrade(actor,school.Id,1,"أ");
        db.SaveStudent(actor,new SchoolStudent("",school.Id,"100","طالب",1,"أ","","","","",true,"2026/2027"));
        var student=Assert.Single(db.GetStudents(actor,school.Id));
        db.SaveStudentResult(actor,school.Id,student.Id,"الفصل الأول","عربي",88,100,"");
        using(var connection=DatabaseSecurity.OpenForRegressionTest(path))
        using(var version=connection.CreateCommand())
        {
            version.CommandText="PRAGMA user_version=4;";
            version.ExecuteNonQuery();
        }
        db.Initialize();
        Assert.Equal(student.Id,Assert.Single(db.GetStudents(actor,school.Id)).Id);
        Assert.Single(db.GetStudentResults(actor,school.Id,student.Id));
        using(var connection=DatabaseSecurity.OpenForRegressionTest(path))
        using(var version=connection.CreateCommand())
        {
            version.CommandText="PRAGMA user_version;";
            Assert.Equal(5L,Convert.ToInt64(version.ExecuteScalar()));
        }
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!,"recovery"),
            "before-schema-*.db"));
    }

    [Fact]
    public void PreviewShowsOldNewAndBlankCellsDoNotEraseExistingValues()
    {
        var (db, actor, school) = New();
        var table = Table(["code","name","zone"], ["A01","الاسم المعدل",""]);
        var preview = Plan(db, actor, ImportEntity.Schools, "update.csv", table);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowStatus.Update, row.Status);
        var change = Assert.Single(row.Changes);
        Assert.Equal("name", change.Field);
        Assert.Equal(school.Name, change.Before);
        Assert.Equal("الاسم المعدل", change.After);
        var batch = db.ApplyImport(actor, preview);
        Assert.Equal(1, batch.Updated);
        Assert.Equal(school.Zone, Assert.Single(db.GetSchools(actor)).Zone);
        Assert.Single(db.GetImportBatches(actor));
    }

    [Fact]
    public void InvalidAndDuplicateRowsBlockTheWholeBatch()
    {
        var (db, actor, school) = New();
        var table = Table(["code","name","zone","ownership","stage"],
            ["A01","اسم جديد","","",""],
            ["N01","مدرسة","منطقة","حكومية","أساسي"],
            ["N01","مكرر","منطقة","حكومية","أساسي"],
            ["N02","","منطقة","حكومية","أساسي"]);
        var preview = Plan(db, actor, ImportEntity.Schools, "invalid.csv", table);
        Assert.Contains(preview.Rows, x => x.Status == ImportRowStatus.Duplicate);
        Assert.Contains(preview.Rows, x => x.Status == ImportRowStatus.Invalid);
        Assert.Throws<InvalidOperationException>(() => db.ApplyImport(actor, preview));
        Assert.Equal(school.Name, Assert.Single(db.GetSchools(actor)).Name);
        Assert.Empty(db.GetImportBatches(actor));
    }

    [Fact]
    public void StalePreviewNeverOverwritesLaterChanges()
    {
        var (db, actor, school) = New();
        var table = Table(["code","name"], ["A01","اسم مستورد"]);
        var preview = Plan(db, actor, ImportEntity.Schools, "stale.csv", table);
        db.UpdateSchool(actor, school with { Name = "تعديل لاحق" });
        Assert.Throws<InvalidOperationException>(() => db.ApplyImport(actor, preview));
        Assert.Equal("تعديل لاحق", Assert.Single(db.GetSchools(actor)).Name);
    }

    [Fact]
    public void StalePreviewAlsoRejectsChangesToOtherFieldsNotIncludedInImport()
    {
        var (db,actor,school)=New();
        var source=Table(["code","name"],["A01","اسم مستورد"]);
        var preview=Plan(db,actor,ImportEntity.Schools,"stale-elsewhere.csv",source);
        db.UpdateSchool(actor,school with { Zone="تعديل موقع لاحق" });
        Assert.Throws<InvalidOperationException>(()=>db.ApplyImport(actor,preview));
        var current=Assert.Single(db.GetSchools(actor));
        Assert.Equal("تعديل موقع لاحق",current.Zone);
        Assert.Equal(school.Name,current.Name);
    }

    [Fact]
    public void RollbackIsAuditedAndRefusesSubsequentEdits()
    {
        var (db, actor, school) = New();
        var table = Table(["code","name"], ["A01","بعد الاستيراد"]);
        var batch = db.ApplyImport(actor, Plan(db, actor, ImportEntity.Schools, "rev.csv", table));
        db.ReverseImport(actor, batch.Id);
        Assert.Equal(school.Name, Assert.Single(db.GetSchools(actor)).Name);
        Assert.Equal("reversed", Assert.Single(db.GetImportBatches(actor)).Status);
        var next = db.ApplyImport(actor, Plan(db, actor, ImportEntity.Schools, "rev2.csv", table));
        db.UpdateSchool(actor, Assert.Single(db.GetSchools(actor)) with { Zone = "تعديل لاحق" });
        Assert.Throws<InvalidOperationException>(() => db.ReverseImport(actor, next.Id));
        Assert.Equal("applied", db.GetImportBatches(actor).Single(x => x.Id == next.Id).Status);
    }
}
