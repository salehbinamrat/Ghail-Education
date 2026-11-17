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
            var key=entity==ImportEntity.Schools?change.After["code"] :
                change.After["employee_code"];
            var loaded=ImportExisting(context,entity,change.SchoolId,key);
            if(loaded is null || loaded.Id!=change.Id ||
                !ImportFieldsEqual(loaded.Fields,change.After))
                throw new InvalidOperationException(
                    $"صف {change.Row}: تغيّرت بيانات السجل منذ الاستيراد؛ التراجع قد يمحو تعديلا لاحقا.");
            using(var newer=Cmd(con,tx,"""
                SELECT EXISTS(SELECT 1 FROM audit_events
                    WHERE entity_id=$entity AND created_utc>$since)
                """,("$entity",change.Id),("$since",appliedAt)))
                if(Convert.ToInt32(newer.ExecuteScalar(),CultureInfo.InvariantCulture)!=0)
                    throw new InvalidOperationException(
                        $"صف {change.Row}: توجد عملية لاحقة مسجلة على هذا السجل؛ رفض التراجع حفاظا عليها.");
        }
        // No partial reversal: all preconditions and every SQL write share one transaction.
        try
        {
            foreach(var change in changes)
            {
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
