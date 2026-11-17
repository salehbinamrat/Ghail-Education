using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    private static void MigratePhase4Import(SqliteConnection con)
    {
        using var cmd=Cmd(con,null,"""
            CREATE TABLE IF NOT EXISTS import_batches(
                id TEXT PRIMARY KEY,
                entity_type TEXT NOT NULL CHECK(entity_type IN ('Schools','Students','Staff','Sections')),
                source_name TEXT NOT NULL,
                source_sha256 TEXT NOT NULL,
                academic_year TEXT NOT NULL,
                actor_id TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
                created INTEGER NOT NULL,
                updated INTEGER NOT NULL,
                unchanged INTEGER NOT NULL,
                rejected INTEGER NOT NULL,
                warnings TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL CHECK(status IN ('applied','reversed')),
                imported_utc TEXT NOT NULL,
                reversed_utc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS import_changes(
                batch_id TEXT NOT NULL REFERENCES import_batches(id) ON DELETE RESTRICT,
                ordinal INTEGER NOT NULL,
                entity_id TEXT NOT NULL,
                school_id TEXT NOT NULL,
                operation TEXT NOT NULL CHECK(operation IN ('new','update')),
                before_json TEXT NOT NULL,
                after_json TEXT NOT NULL,
                PRIMARY KEY(batch_id,ordinal)
            );
            CREATE INDEX IF NOT EXISTS ix_import_actor_date ON import_batches(actor_id,imported_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_import_change_entity ON import_changes(entity_id);
            """);
        cmd.ExecuteNonQuery();
    }

    private static ImportRecord? ImportExisting(ImportContext state, ImportEntity type,
        string schoolId, string code) => type switch {
            ImportEntity.Schools => state.Schools.GetValueOrDefault(code),
            ImportEntity.Students => state.Students.GetValueOrDefault(ImportKey(schoolId,code)),
            _ => state.Staff.GetValueOrDefault(ImportKey(schoolId,code))
        };

    private static Dictionary<string,string> ApplyFieldChanges(ImportEntity type,
        ImportRecord? old, string schoolId, string schoolCode, string recordCode,
        IReadOnlyList<ImportFieldChange> changes)
    {
        var values=old is null ? new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string,string>(old.Fields,StringComparer.OrdinalIgnoreCase);
        if(type==ImportEntity.Schools) values["code"]=old?.Fields["code"]??recordCode;
        else
        {
            values["school_id"]=schoolId;
            values["school_code"]=schoolCode;
            values[type==ImportEntity.Students ? "student_code" : "employee_code"]=recordCode;
        }
        foreach(var change in changes) values[change.Field]=change.After;
        if(old is null)
        {
            values["is_active"]="1";
            if(type==ImportEntity.Students)
            {
                values.TryAdd("guardian_name","");
                values.TryAdd("guardian_phone","");
                values.TryAdd("notes","");
                values.TryAdd("section","أ");
                values["origin_school_id"]=schoolId;
                values["has_current"]="1";
            }
            if(type==ImportEntity.Staff)
            {
                values.TryAdd("job_title","");
                values.TryAdd("specialization","");
                values.TryAdd("qualification","");
            }
            if(type==ImportEntity.Schools) values.TryAdd("gender","مختلطة");
            if(type==ImportEntity.Staff) values.TryAdd("gender","غير محدد");
        }
        return values;
    }

    private static string RequiredImport(Dictionary<string,string> data,string key) =>
        data.TryGetValue(key,out var value)?value:"";

    private static string LookupInput(IReadOnlyList<string> row,
        IReadOnlyDictionary<string,int> mapping,string field) =>
        mapping.TryGetValue(field,out var col) && col<row.Count ? row[col].Trim():"";

    /// <summary>
    /// Single transaction: recompute preview under the write lock before any mutation,
    /// then perform all valid inserts/updates, lineage and audit or roll back everything.
    /// </summary>
    public ImportBatchSummary ApplyImport(UserAccount actor,ImportPreview preview)
    {
        RequireFreshActor(actor);
        ArgumentNullException.ThrowIfNull(preview);
        CheckImportTable(preview.Entity,preview.SourceName,preview.Table,preview.ColumnMap);
        using var con=Open();
        using var tx=con.BeginTransaction(deferred:false);
        var refreshed=BuildImportPreview(con,tx,actor,preview.Entity,preview.SourceName,
            preview.Table,preview.ColumnMap,preview.DefaultSchoolId);
        if(refreshed.SourceSha256!=preview.SourceSha256 ||
           refreshed.StateSha256!=preview.StateSha256 ||
           refreshed.AcademicYear!=preview.AcademicYear)
            throw new InvalidOperationException(
                "تغير الملف أو أحد السجلات منذ المعاينة. راجع الفروق مجددا قبل الاعتماد.");
        if(!refreshed.CanCommit)
            throw new InvalidOperationException(
                "تحتوي المعاينة على صفوف غير صالحة أو مكررة أو متعارضة. أصلحها قبل استيراد الدفعة كاملة.");
        var context=LoadImportContext(con,tx);
        var batchId=Id();
        var now=Utc();
        var warnings=preview.Entity switch
        {
            ImportEntity.Students => "سجلات الطلاب ذات تاريخ دراسي؛ لا يدعم النظام التراجع الآلي عن دفعاتهم.",
            ImportEntity.Sections => "يمكن التراجع عن الشعب الجديدة فقط إذا لم ترتبط بطلاب أو تاريخ دراسي.",
            _ => ""
        };
        using(var batch=Cmd(con,tx,"""
            INSERT INTO import_batches(id,entity_type,source_name,source_sha256,academic_year,
                actor_id,created,updated,unchanged,rejected,warnings,status,imported_utc)
            VALUES($id,$entity,$source,$hash,$year,$actor,$new,$changed,$same,0,$warnings,'applied',$now)
            """,("$id",batchId),("$entity",preview.Entity.ToString()),
            ("$source",Path.GetFileName(preview.SourceName)),("$hash",preview.SourceSha256),
            ("$year",refreshed.AcademicYear),("$actor",actor.Id),
            ("$new",refreshed.Created),("$changed",refreshed.Updated),
            ("$same",refreshed.Unchanged),("$warnings",warnings),("$now",now)))
            batch.ExecuteNonQuery();

        foreach(var plan in refreshed.Rows)
        {
            if(plan.Status==ImportRowStatus.Unchanged) continue;
            var input=preview.Table.Rows[plan.RowNumber-2];
            if(preview.Entity==ImportEntity.Sections)
            {
                var schoolCode=LookupInput(input,preview.ColumnMap,"school_code");
                if(schoolCode.Length==0 && preview.DefaultSchoolId is not null)
                    schoolCode=context.Schools.Values.Single(x=>x.Id==preview.DefaultSchoolId).Fields["code"];
                var school=context.Schools[schoolCode];
                if(!GradeNames.TryParse(LookupInput(input,preview.ColumnMap,"grade"),out var grade))
                    throw new InvalidOperationException("تغير الصف منذ المعاينة.");
                var section=LookupInput(input,preview.ColumnMap,"section");
                var gradeId=Id();
                using(var create=Cmd(con,tx,"""
                    INSERT INTO school_grades(id,school_id,academic_year,grade,section,created_utc)
                    VALUES($id,$school,$year,$grade,$section,$now)
                    """,("$id",gradeId),("$school",school.Id),("$year",refreshed.AcademicYear),
                    ("$grade",grade),("$section",section),("$now",Utc())))
                    create.ExecuteNonQuery();
                var sectionAfter=JsonSerializer.Serialize(Fields(("school_id",school.Id),
                    ("school_code",schoolCode),("academic_year",refreshed.AcademicYear),
                    ("grade",grade.ToString(CultureInfo.InvariantCulture)),("section",section)));
                using var sectionChange=Cmd(con,tx,"""
                    INSERT INTO import_changes(batch_id,ordinal,entity_id,school_id,operation,before_json,after_json)
                    VALUES($batch,$row,$id,$school,'new','',$after)
                    """,("$batch",batchId),("$row",plan.RowNumber),("$id",gradeId),
                    ("$school",school.Id),("$after",sectionAfter));
                sectionChange.ExecuteNonQuery();
                continue;
            }
            var sourceCode=LookupInput(input,preview.ColumnMap,"school_code");
            string schoolId="";
            if(preview.Entity!=ImportEntity.Schools)
            {
                if(sourceCode.Length==0 && preview.DefaultSchoolId is not null)
                    sourceCode=context.Schools.Values.Single(x=>x.Id==preview.DefaultSchoolId).Fields["code"];
                if(!context.Schools.TryGetValue(sourceCode,out var school))
                    throw new InvalidOperationException("تغيرت المدرسة أثناء تنفيذ الاستيراد.");
                schoolId=school.Id;
            }
            var code=LookupInput(input,preview.ColumnMap,preview.Entity switch{
                ImportEntity.Schools=>"code",ImportEntity.Students=>"student_code",_=>"employee_code"});
            var old=ImportExisting(context,preview.Entity,schoolId,code);
            var next=ApplyFieldChanges(preview.Entity,old,schoolId,sourceCode,code,plan.Changes);
            var entityId=old?.Id??Id();
            var before=old is null ? "" : JsonSerializer.Serialize(old.Fields);
            var after=JsonSerializer.Serialize(next);
            switch(preview.Entity)
            {
                case ImportEntity.Schools:
                    WriteImportedSchool(con,tx,entityId,old is null,next);
                    schoolId=entityId;
                    break;
                case ImportEntity.Staff:
                    WriteImportedStaff(con,tx,entityId,old is null,next);
                    break;
                case ImportEntity.Students:
                    WriteImportedStudent(con,tx,entityId,old is null,next,refreshed.AcademicYear,context.Grades);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(preview));
            }
            using var change=Cmd(con,tx,"""
                INSERT INTO import_changes(batch_id,ordinal,entity_id,school_id,operation,before_json,after_json)
                VALUES($batch,$row,$id,$school,$op,$before,$after)
                """,("$batch",batchId),("$row",plan.RowNumber),("$id",entityId),
                ("$school",schoolId),("$op",old is null?"new":"update"),
                ("$before",before),("$after",after));
            change.ExecuteNonQuery();
        }
        Audit(con,tx,actor.Id,"import.apply",batchId,
            $"type={preview.Entity};created={refreshed.Created};updated={refreshed.Updated};unchanged={refreshed.Unchanged};sha256={preview.SourceSha256}");
        tx.Commit();
        return new ImportBatchSummary(batchId,preview.Entity,Path.GetFileName(preview.SourceName),
            refreshed.AcademicYear,now,actor.Username,refreshed.Created,refreshed.Updated,
            refreshed.Unchanged,0,"applied",warnings);
    }

    private static void WriteImportedSchool(SqliteConnection con,SqliteTransaction tx,
        string id,bool created,Dictionary<string,string> v)
    {
        var stage=RequiredImport(v,"stage");
        var kindergarten=stage=="روضة";
        if(created)
        {
            using var add=Cmd(con,tx,"""
                INSERT INTO schools(id,code,name,zone,ownership,stage,gender,is_kindergarten,created_utc)
                VALUES($id,$code,$name,$zone,$ownership,$stage,$gender,$k,$now)
                """,("$id",id),("$code",RequiredImport(v,"code")),
                ("$name",RequiredImport(v,"name")),("$zone",RequiredImport(v,"zone")),
                ("$ownership",RequiredImport(v,"ownership")),("$stage",DatabaseStage(stage)),
                ("$gender",RequiredImport(v,"gender")),("$k",kindergarten?1:0),("$now",Utc()));
            add.ExecuteNonQuery();
        }
        else
        {
            EnsureSchoolStructureCompatible(con,tx,id,DatabaseStage(stage),RequiredImport(v,"gender"));
            EnsureKindergartenTransitionCompatible(con,tx,id,kindergarten);
            using var update=Cmd(con,tx,"""
                UPDATE schools SET name=$name,zone=$zone,ownership=$ownership,
                    stage=$stage,gender=$gender,is_kindergarten=$k
                WHERE id=$id AND is_active=1
                """,("$id",id),("$name",RequiredImport(v,"name")),
                ("$zone",RequiredImport(v,"zone")),("$ownership",RequiredImport(v,"ownership")),
                ("$stage",DatabaseStage(stage)),("$gender",RequiredImport(v,"gender")),
                ("$k",kindergarten?1:0));
            if(update.ExecuteNonQuery()!=1) throw new InvalidOperationException("تعذر تعديل المدرسة أثناء الدفعة.");
        }
    }

    private static void WriteImportedStaff(SqliteConnection con,SqliteTransaction tx,
        string id,bool created,Dictionary<string,string> v)
    {
        if(created)
        {
            using var add=Cmd(con,tx,"""
                INSERT INTO school_staff(id,school_id,employee_code,full_name,category,
                    job_title,specialization,qualification,gender,is_active,created_utc,updated_utc)
                VALUES($id,$school,$code,$name,$category,$job,$specialty,$qualification,$gender,1,$now,$now)
                """,("$id",id),("$school",RequiredImport(v,"school_id")),
                ("$code",RequiredImport(v,"employee_code")),("$name",RequiredImport(v,"full_name")),
                ("$category",RequiredImport(v,"category")),("$job",RequiredImport(v,"job_title")),
                ("$specialty",RequiredImport(v,"specialization")),
                ("$qualification",RequiredImport(v,"qualification")),
                ("$gender",RequiredImport(v,"gender")),("$now",Utc()));
            add.ExecuteNonQuery();
        }
        else
        {
            using var edit=Cmd(con,tx,"""
                UPDATE school_staff SET full_name=$name,category=$category,
                    job_title=$job,specialization=$specialty,qualification=$qualification,
                    gender=$gender,updated_utc=$now
                WHERE id=$id AND school_id=$school AND is_active=1
                """,("$id",id),("$school",RequiredImport(v,"school_id")),
                ("$name",RequiredImport(v,"full_name")),("$category",RequiredImport(v,"category")),
                ("$job",RequiredImport(v,"job_title")),
                ("$specialty",RequiredImport(v,"specialization")),
                ("$qualification",RequiredImport(v,"qualification")),
                ("$gender",RequiredImport(v,"gender")),("$now",Utc()));
            if(edit.ExecuteNonQuery()!=1) throw new InvalidOperationException("تعذر تعديل الموظف أثناء الدفعة.");
        }
    }

    private static void WriteImportedStudent(SqliteConnection con,SqliteTransaction tx,
        string id,bool created,Dictionary<string,string> v,string year,
        IReadOnlyDictionary<string,string> grades)
    {
        var schoolId=RequiredImport(v,"school_id");
        var grade=RequiredImport(v,"grade");
        var section=RequiredImport(v,"section");
        if(!grades.TryGetValue(GradeKey(schoolId,grade,section),out var gradeId))
            throw new InvalidOperationException("تغير الصف أو الشعبة بعد المعاينة.");
        if(created)
        {
            using(var add=Cmd(con,tx,"""
                INSERT INTO school_students(id,school_id,student_code,full_name,gender,
                    guardian_name,guardian_phone,notes,is_active,created_utc,updated_utc)
                VALUES($id,$school,$code,$name,$gender,$guardian,$phone,'',1,$now,$now)
                """,("$id",id),("$school",schoolId),
                ("$code",RequiredImport(v,"student_code")),("$name",RequiredImport(v,"full_name")),
                ("$gender",RequiredImport(v,"gender")),
                ("$guardian",RequiredImport(v,"guardian_name")),
                ("$phone",RequiredImport(v,"guardian_phone")),("$now",Utc())))
                add.ExecuteNonQuery();
            using(var enrollment=Cmd(con,tx,"""
                INSERT INTO student_enrollments(student_id,school_id,academic_year,grade_id,status,updated_utc)
                VALUES($id,$school,$year,$grade,'مسجل',$now)
                """,("$id",id),("$school",schoolId),("$year",year),("$grade",gradeId),("$now",Utc())))
                enrollment.ExecuteNonQuery();
        }
        else
        {
            using(var update=Cmd(con,tx,"""
                UPDATE school_students SET full_name=$name,gender=$gender,
                    guardian_name=$guardian,guardian_phone=$phone,updated_utc=$now
                WHERE id=$id AND school_id=$school AND is_active=1
                """,("$id",id),("$school",schoolId),
                ("$name",RequiredImport(v,"full_name")),("$gender",RequiredImport(v,"gender")),
                ("$guardian",RequiredImport(v,"guardian_name")),
                ("$phone",RequiredImport(v,"guardian_phone")),("$now",Utc())))
                if(update.ExecuteNonQuery()!=1)
                    throw new InvalidOperationException("تعذر تحديث هوية الطالب أثناء الدفعة.");
            // A student import may correct identity fields, but may NOT perform
            // grade/section transfers or rewrite academic placement timestamps.
            // Those are exclusively managed by the controlled Phase 2 workflow.
            using(var placement=Cmd(con,tx,"""
                SELECT EXISTS(
                    SELECT 1 FROM student_enrollments
                    WHERE student_id=$id AND school_id=$school AND academic_year=$year
                      AND grade_id=$grade AND status='مسجل')
                """,("$id",id),("$school",schoolId),("$year",year),("$grade",gradeId)))
                if(Convert.ToInt32(placement.ExecuteScalar(),CultureInfo.InvariantCulture)!=1)
                    throw new InvalidOperationException(
                        "القيد الدراسي لم يعد مطابقا للمعاينة؛ استخدم إجراء النقل أو الترفيع وليس الاستيراد.");
        }
    }
}
