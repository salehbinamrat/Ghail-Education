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
