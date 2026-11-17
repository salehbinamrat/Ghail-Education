using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    public IReadOnlyList<ImportBatchSummary> GetImportBatches(UserAccount actor)
    {
        RequireFreshActor(actor);
        if(!AccessPolicy.Has(actor,PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException("سجل الدفعات يتطلب صلاحية المدارس.");
        using var con=Open();
        using var cmd=Cmd(con,null,"""
            SELECT b.id,b.entity_type,b.source_name,b.academic_year,b.imported_utc,
                   u.username,b.created,b.updated,b.unchanged,b.rejected,b.status,b.warnings
            FROM import_batches b JOIN users u ON u.id=b.actor_id
            WHERE b.actor_id=$actor OR $owner=1
            ORDER BY b.imported_utc DESC,b.id DESC LIMIT 250
            """,("$actor",actor.Id),("$owner",actor.IsOwner?1:0));
        using var r=cmd.ExecuteReader();
        var result=new List<ImportBatchSummary>();
        while(r.Read())
            result.Add(new ImportBatchSummary(Text(r,0),
                Enum.Parse<ImportEntity>(Text(r,1)),Text(r,2),Text(r,3),
                Text(r,4),Text(r,5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),
                r.GetInt32(9),Text(r,10),Text(r,11)));
        return result;
    }

    /// <summary>Inspectable, permission-scoped history of every row's applied field values.</summary>
    public IReadOnlyList<ImportBatchChangeRow> GetImportBatchChanges(UserAccount actor,string batchId)
    {
        RequireFreshActor(actor);
        if(!AccessPolicy.Has(actor,PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض تفاصيل الاستيراد.");
        using var con=Open();
        ImportEntity kind;
        string author;
        using(var cmd=Cmd(con,null,
            "SELECT entity_type,actor_id FROM import_batches WHERE id=$id",("$id",batchId)))
        using(var r=cmd.ExecuteReader())
        {
            if(!r.Read()) throw new ArgumentException("الدفعة غير موجودة.");
            kind=Enum.Parse<ImportEntity>(Text(r,0));
            author=Text(r,1);
        }
        if(!actor.IsOwner && author!=actor.Id ||
            kind==ImportEntity.Staff && !AccessPolicy.Has(actor,PermissionFlags.ViewStaff) ||
            (kind==ImportEntity.Students || kind==ImportEntity.Sections) &&
                !AccessPolicy.Has(actor,PermissionFlags.ViewStudents))
            throw new UnauthorizedAccessException("ليس لديك صلاحية الاطلاع على تفاصيل هذه الدفعة.");
        var list=new List<ImportBatchChangeRow>();
        using var changes=Cmd(con,null,"""
            SELECT ordinal,entity_id,operation,before_json,after_json
            FROM import_changes WHERE batch_id=$id ORDER BY ordinal
            """,("$id",batchId));
        using var reader=changes.ExecuteReader();
        while(reader.Read())
        {
            var before=reader.IsDBNull(3) || Text(reader,3).Length==0 ?
                new Dictionary<string,string>() : DecodeImportSnapshot(Text(reader,3));
            var after=DecodeImportSnapshot(Text(reader,4));
            var details=before.Keys.Union(after.Keys,StringComparer.OrdinalIgnoreCase)
                .OrderBy(x=>x,StringComparer.Ordinal)
                .Where(key=>before.GetValueOrDefault(key,"")!=after.GetValueOrDefault(key,""))
                .Select(key=>new ImportFieldChange(key,before.GetValueOrDefault(key,""),
                    after.GetValueOrDefault(key,""))).ToArray();
            list.Add(new ImportBatchChangeRow(reader.GetInt32(0),Text(reader,1),
                Text(reader,2),details));
        }
        return list;
    }

    private sealed record StoredImportChange(int Row,string Id,string SchoolId,string Operation,
        Dictionary<string,string> Before,Dictionary<string,string> After);

    private static Dictionary<string,string> DecodeImportSnapshot(string json) =>
        JsonSerializer.Deserialize<Dictionary<string,string>>(json) ??
            throw new InvalidDataException("بيانات استعادة الدفعة غير مكتملة.");

    private static bool ImportFieldsEqual(IReadOnlyDictionary<string,string> first,
        IReadOnlyDictionary<string,string> second) =>
        first.Count==second.Count && first.All(pair=>second.TryGetValue(pair.Key,out var value) &&
            string.Equals(value,pair.Value,StringComparison.Ordinal));

    /// <summary>
    /// Conservative complete-batch compensation. If any row has drift, later
    /// audit activity, or dependent data, refuse the entire reverse transaction.
    /// Student enrollment history is never removed by automatic compensation.
    /// </summary>
    public void ReverseImport(UserAccount actor,string batchId)
    {
        RequireFreshActor(actor);
        if(string.IsNullOrWhiteSpace(batchId)) throw new ArgumentException("حدد دفعة.");
        using var con=Open();
        using var tx=con.BeginTransaction(deferred:false);
        ImportEntity entity;
        string status,submittedBy,appliedAt;
        using(var head=Cmd(con,tx,"""
            SELECT entity_type,status,actor_id,imported_utc FROM import_batches WHERE id=$id
            """,("$id",batchId)))
        using(var r=head.ExecuteReader())
        {
            if(!r.Read()) throw new ArgumentException("الدفعة غير موجودة.");
            entity=Enum.Parse<ImportEntity>(Text(r,0));
            status=Text(r,1);submittedBy=Text(r,2);appliedAt=Text(r,3);
        }
        if(status!="applied") throw new InvalidOperationException("هذه الدفعة أُلغيت سابقا.");
        if(!actor.IsOwner && submittedBy!=actor.Id)
            throw new UnauthorizedAccessException("التراجع متاح لمن استورد الدفعة أو للمسؤول المؤسس.");
        if(entity==ImportEntity.Students)
            throw new InvalidOperationException(
                "لا يسمح بالتراجع الآلي عن قيود الطلاب لأنها قد أصبحت جزءا من التاريخ الدراسي؛ استخدم إجراءات النقل أو الأرشفة المصرح بها.");
        if(entity==ImportEntity.Schools && !AccessPolicy.CanEditSchool(actor))
            throw new UnauthorizedAccessException("لا تملك صلاحية إدارة المدارس.");
        var changes=new List<StoredImportChange>();
        using(var read=Cmd(con,tx,"""
            SELECT ordinal,entity_id,school_id,operation,before_json,after_json
            FROM import_changes WHERE batch_id=$id ORDER BY ordinal DESC
            """,("$id",batchId)))
        using(var r=read.ExecuteReader())
            while(r.Read())
                changes.Add(new StoredImportChange(r.GetInt32(0),Text(r,1),Text(r,2),
                    Text(r,3),Text(r,4)==""?new Dictionary<string,string>():DecodeImportSnapshot(Text(r,4)),
                    DecodeImportSnapshot(Text(r,5))));
        var context=LoadImportContext(con,tx);
        foreach(var change in changes)
        {
            if(entity==ImportEntity.Staff &&
                !AccessPolicy.CanEditStaff(actor,change.SchoolId))
                throw new UnauthorizedAccessException("لا تملك صلاحية تعديل كادر هذه المدرسة.");
            if(entity==ImportEntity.Sections)
            {
                if(!AccessPolicy.CanManageStudents(actor,change.SchoolId))
                    throw new UnauthorizedAccessException("لا تملك صلاحية تعديل شعب هذه المدرسة.");
                if(change.After["academic_year"]!=context.AcademicYear)
                    throw new InvalidOperationException("تغير العام النشط؛ لا يمكن عكس شعبة من عام آخر.");
                RequireOpenYear(con,tx,context.AcademicYear);
                var gradeKey=GradeKey(change.SchoolId,change.After["grade"],change.After["section"]);
                if(!context.Grades.TryGetValue(gradeKey,out var currentGradeId) ||
                    currentGradeId!=change.Id)
                    throw new InvalidOperationException($"صف {change.Row}: الشعبة تغيرت منذ الاستيراد.");
                if(!context.Schools.TryGetValue(change.After["school_code"],out var school) ||
                    school.Id!=change.SchoolId)
                    throw new InvalidOperationException("رمز المدرسة تغير منذ استيراد الشعبة.");
            }
            else
            {
                var key=entity==ImportEntity.Schools?change.After["code"] :
                    change.After["employee_code"];
                var loaded=ImportExisting(context,entity,change.SchoolId,key);
                if(loaded is null || loaded.Id!=change.Id ||
                    !ImportFieldsEqual(loaded.Fields,change.After))
                    throw new InvalidOperationException(
                        $"صف {change.Row}: تغيّرت بيانات السجل منذ الاستيراد؛ التراجع قد يمحو تعديلا لاحقا.");
            }
            using(var newer=Cmd(con,tx,"""
                SELECT EXISTS(SELECT 1 FROM audit_events
                    WHERE entity_id=$entity AND created_utc>$since)
                """,("$entity",change.Id),("$since",appliedAt)))
                if(Convert.ToInt32(newer.ExecuteScalar(),CultureInfo.InvariantCulture)!=0)
                    throw new InvalidOperationException(
                        $"صف {change.Row}: توجد عملية لاحقة مسجلة على هذا السجل؛ رفض التراجع حفاظا عليها.");
            using(var subsequent=Cmd(con,tx,"""
                SELECT EXISTS(
                    SELECT 1 FROM import_changes later
                    JOIN import_batches batch ON batch.id=later.batch_id
                    WHERE later.entity_id=$entity AND later.batch_id<>$batch
                      AND batch.imported_utc>=$since)
                """,("$entity",change.Id),("$batch",batchId),("$since",appliedAt)))
                if(Convert.ToInt32(subsequent.ExecuteScalar(),CultureInfo.InvariantCulture)!=0)
                    throw new InvalidOperationException(
                        $"صف {change.Row}: توجد دفعة استيراد لاحقة على السجل؛ لا يجوز التراجع حتى لو تساوت القيم الحالية.");

        }
        // No partial reversal: all preconditions and every SQL write share one transaction.
        try
        {
            foreach(var change in changes)
            {
                if(entity==ImportEntity.Sections)
                {
                    using var delete=Cmd(con,tx,"DELETE FROM school_grades WHERE id=$id AND school_id=$school AND academic_year=$year",
                        ("$id",change.Id),("$school",change.SchoolId),
                        ("$year",change.After["academic_year"]));
                    if(delete.ExecuteNonQuery()!=1)
                        throw new InvalidOperationException("الشعبة لم تعد موجودة.");
                    continue;
                }
                if(change.Operation=="new")
                {
                    if(entity==ImportEntity.Schools)
                    {
                        using var remove=Cmd(con,tx,"DELETE FROM schools WHERE id=$id AND is_active=1",
                            ("$id",change.Id));
                        if(remove.ExecuteNonQuery()!=1) throw new InvalidOperationException("مدرسة جديدة غير موجودة.");
                    }
                    else
                    {
                        // Only untouched original assignment may be removed. Never
                        // erase a real later secondment, career or personnel profile.
                        using(var count=Cmd(con,tx,"""
                            SELECT COUNT(*) FROM employee_assignments
                            WHERE employee_id=$id AND assignment_type='أصلي' AND
                                start_date IS NULL AND end_date IS NULL AND
                                notes='سجل أصلي'
                            """,("$id",change.Id)))
                            if(Convert.ToInt32(count.ExecuteScalar(),CultureInfo.InvariantCulture)!=1)
                                throw new InvalidOperationException("تكليف الموظف تغير بعد الاستيراد.");
                        using(var total=Cmd(con,tx,"SELECT COUNT(*) FROM employee_assignments WHERE employee_id=$id",
                            ("$id",change.Id)))
                            if(Convert.ToInt32(total.ExecuteScalar(),CultureInfo.InvariantCulture)!=1)
                                throw new InvalidOperationException("للموظف تاريخ تكليف لاحق لا يمكن حذفه.");
                        using(var child=Cmd(con,tx,"SELECT EXISTS(SELECT 1 FROM staff_personnel_details WHERE staff_id=$id)",
                            ("$id",change.Id)))
                            if(Convert.ToInt32(child.ExecuteScalar(),CultureInfo.InvariantCulture)!=0)
                                throw new InvalidOperationException("ملف الموظف توسع بعد الاستيراد؛ لا يمكن حذفه.");
                        using(var remove=Cmd(con,tx,"DELETE FROM employee_assignments WHERE employee_id=$id",
                            ("$id",change.Id))) remove.ExecuteNonQuery();
                        using(var remove=Cmd(con,tx,"DELETE FROM employee_identities WHERE id=$id",
                            ("$id",change.Id))) remove.ExecuteNonQuery();
                        using(var remove=Cmd(con,tx,"DELETE FROM school_staff WHERE id=$id",
                            ("$id",change.Id)))
                            if(remove.ExecuteNonQuery()!=1) throw new InvalidOperationException("تعذر إزالة سجل الكادر.");
                    }
                }
                else if(change.Operation=="update")
                {
                    if(entity==ImportEntity.Schools)
                        WriteImportedSchool(con,tx,change.Id,false,change.Before);
                    else
                        WriteImportedStaff(con,tx,change.Id,false,change.Before);
                }
            }
        }
        catch(SqliteException ex)
        {
            throw new InvalidOperationException(
                "رفض التراجع: توجد بيانات أو روابط أو سجلات لاحقة تعتمد على هذه الدفعة، ولم يُحذف أي سجل.",ex);
        }
        using(var mark=Cmd(con,tx,"""
            UPDATE import_batches SET status='reversed',reversed_utc=$now
            WHERE id=$id AND status='applied'
            """,("$now",Utc()),("$id",batchId)))
            if(mark.ExecuteNonQuery()!=1) throw new InvalidOperationException("تغيرت حالة الدفعة.");
        Audit(con,tx,actor.Id,"import.reverse",batchId,$"type={entity};changes={changes.Count}");
        tx.Commit();
    }
}
