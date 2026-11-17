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
