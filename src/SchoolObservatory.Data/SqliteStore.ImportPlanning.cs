using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    private sealed record ImportRecord(string Id, Dictionary<string,string> Fields);

    private sealed class ImportContext
    {
        public Dictionary<string,ImportRecord> Schools { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,ImportRecord> Staff { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,ImportRecord> Students { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,HashSet<string>> StaffOwners { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,string> Grades { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string AcademicYear { get; set; } = "";
    }

    private static string ImportKey(string schoolId, string code) => schoolId + "\u001f" + code.Trim();
    private static string GradeKey(string schoolId, string grade, string section) =>
        schoolId + "\u001f" + grade + "\u001f" + section;
    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Dictionary<string,string> Fields(params (string Key,string Value)[] pairs) =>
        pairs.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

    private static ImportContext LoadImportContext(SqliteConnection con, SqliteTransaction tx)
    {
        var state = new ImportContext();
        using (var cmd = Cmd(con, tx, "SELECT value FROM settings WHERE key='academic_year'"))
            state.AcademicYear = cmd.ExecuteScalar()?.ToString() ?? "";
        using (var cmd = Cmd(con, tx, """
            SELECT id,code,name,zone,ownership,
              CASE WHEN is_kindergarten=1 THEN 'روضة'
                   WHEN stage='أساسي وثانوي' THEN 'مجمع تربوي'
                   ELSE stage END,gender,is_active FROM schools
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var d = Fields(("code",Text(r,1)), ("name",Text(r,2)),("zone",Text(r,3)),
                    ("ownership",Text(r,4)),("stage",Text(r,5)),("gender",Text(r,6)),
                    ("is_active",r.GetInt32(7).ToString(CultureInfo.InvariantCulture)));
                state.Schools.Add(Text(r,1), new ImportRecord(Text(r,0),d));
            }
        using (var cmd = Cmd(con, tx, """
            SELECT st.id,st.school_id,s.code,st.employee_code,st.full_name,st.category,
                   st.job_title,st.specialization,st.qualification,st.gender,st.is_active
            FROM school_staff st JOIN schools s ON s.id=st.school_id
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var d=Fields(("school_id",Text(r,1)),("school_code",Text(r,2)),
                    ("employee_code",Text(r,3)),("full_name",Text(r,4)),("category",Text(r,5)),
                    ("job_title",Text(r,6)),("specialization",Text(r,7)),
                    ("qualification",Text(r,8)),("gender",Text(r,9)),
                    ("is_active",r.GetInt32(10).ToString(CultureInfo.InvariantCulture)));
                state.Staff[ImportKey(Text(r,1),Text(r,3))] = new ImportRecord(Text(r,0),d);
                if (!state.StaffOwners.TryGetValue(Text(r,3).Trim(), out var owners))
                    state.StaffOwners[Text(r,3).Trim()] = owners = new HashSet<string>(StringComparer.Ordinal);
                owners.Add(Text(r,1));
            }
        using (var cmd = Cmd(con, tx, """
            SELECT st.id,local.school_id,s.code,local.student_code,st.full_name,st.gender,
                   st.guardian_name,st.guardian_phone,st.notes,st.is_active,st.school_id,
                   g.grade,g.section
            FROM student_school_codes local
            JOIN school_students st ON st.id=local.student_id
            JOIN schools s ON s.id=local.school_id
            LEFT JOIN student_history h ON h.student_id=st.id
               AND h.school_id=local.school_id AND h.academic_year=$year
               AND h.status='مسجل' AND h.end_utc IS NULL
            LEFT JOIN school_grades g ON g.id=h.grade_id
            """,("$year",state.AcademicYear)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var d=Fields(("school_id",Text(r,1)),("school_code",Text(r,2)),
                    ("student_code",Text(r,3)),("full_name",Text(r,4)),
                    ("gender",Text(r,5)),("guardian_name",Text(r,6)),
                    ("guardian_phone",Text(r,7)),("notes",Text(r,8)),
                    ("is_active",r.GetInt32(9).ToString(CultureInfo.InvariantCulture)),
                    ("origin_school_id",Text(r,10)),
                    ("grade",r.IsDBNull(11)?"":r.GetInt32(11).ToString(CultureInfo.InvariantCulture)),
                    ("section",NullableText(r,12)??""),
                    ("has_current",r.IsDBNull(11)?"0":"1"));
                state.Students[ImportKey(Text(r,1),Text(r,3))] = new ImportRecord(Text(r,0),d);
            }
        using (var cmd = Cmd(con, tx, "SELECT id,school_id,academic_year,grade,section FROM school_grades WHERE academic_year=$y",
            ("$y",state.AcademicYear)))
        using (var r=cmd.ExecuteReader())
            while (r.Read())
                state.Grades[GradeKey(Text(r,1),r.GetInt32(3).ToString(CultureInfo.InvariantCulture),Text(r,4))] = Text(r,0);
        return state;
    }

    private static void CheckImportTable(ImportEntity entity, string sourceName,
        ImportTable table, IReadOnlyDictionary<string,int> mapping)
    {
        if (table.Rows.Count is < 1 or > 5000 || table.Headers.Count is < 1 or > 30)
            throw new ArgumentException("حجم الجدول غير مسموح، الحد الأقصى 5000 صف و30 عمودا.");
        if (table.Rows.Any(r => r.Count>30 || r.Any(v => v.Length>2000)))
            throw new ArgumentException("تجاوزت إحدى الخلايا أو الأعمدة الحد الآمن.");
        if (table.SourceRowNumbers is { } numbers &&
            (numbers.Count != table.Rows.Count ||
             numbers.Any(n => n < 2 || n > 1_048_576) ||
             numbers.Zip(numbers.Skip(1), (a,b) => a >= b).Any(outOfOrder => outOfOrder)))
            throw new ArgumentException("أرقام صفوف المصدر غير صالحة أو غير مرتبة.");
        if (sourceName.Length > 250 || string.IsNullOrWhiteSpace(sourceName))
            throw new ArgumentException("اسم الملف غير صالح.");
        var extension = Path.GetExtension(sourceName).ToLowerInvariant();
        if (!((extension==".csv" && table.Format=="csv") ||
              (extension==".xlsx" && table.Format=="xlsx") ||
              (extension==".docx" && table.Format=="docx")))
            throw new ArgumentException("صيغة الملف لا تطابق امتداده.");
        ImportColumnMapping.Validate(entity, table.Headers, mapping);
    }

    private static string ImportSourceHash(ImportEntity entity, string sourceName,
        ImportTable table, IReadOnlyDictionary<string,int> mapping, string? schoolId) =>
        Digest(JsonSerializer.Serialize(new { entity, sourceName, table,
            mapping = mapping.OrderBy(x=>x.Key,StringComparer.Ordinal).ToArray(),schoolId }));

    private static string ImportPlanHash(string sourceHash, string year,
        IReadOnlyList<ImportPlanRow> rows) =>
        Digest(JsonSerializer.Serialize(new { sourceHash, year, rows }));

    public ImportPreview PreviewImport(UserAccount actor, ImportEntity entity,
        string sourceName, ImportTable table, IReadOnlyDictionary<string,int> mapping,
        string? defaultSchoolId = null)
    {
        RequireFreshActor(actor);
        CheckImportTable(entity,sourceName,table,mapping);
        using var con=Open();
        using var tx=con.BeginTransaction();
        var plan=BuildImportPreview(con,tx,actor,entity,sourceName,table,mapping,defaultSchoolId);
        tx.Commit();
        return plan;
    }

    private ImportPreview BuildImportPreview(SqliteConnection con, SqliteTransaction tx,
        UserAccount actor, ImportEntity entity, string sourceName, ImportTable table,
        IReadOnlyDictionary<string,int> mapping, string? defaultSchoolId)
    {
        if(entity==ImportEntity.Sections)
            return BuildSectionsPreview(con,tx,actor,sourceName,table,mapping,defaultSchoolId);
        if (entity==ImportEntity.Schools && !AccessPolicy.CanEditSchool(actor))
            throw new UnauthorizedAccessException("استيراد المدارس يتطلب صلاحية إدارة المدارس.");
        if (entity!=ImportEntity.Schools && !AccessPolicy.Has(actor,PermissionFlags.ViewSchools))
            throw new UnauthorizedAccessException("صلاحية عرض المدارس مطلوبة.");
        var context=LoadImportContext(con,tx);
        if (entity==ImportEntity.Students)
            RequireOpenYear(con,tx,context.AcademicYear);
        var planned=new List<ImportPlanRow>(table.Rows.Count);
        var seen=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        var seenPayrollCodes=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for(var index=0;index<table.Rows.Count;index++)
        {
            var row=table.Rows[index];
            var line=table.SourceRowNumbers is null ? index+2 : table.SourceRowNumbers[index];
            string Value(string key) => mapping.TryGetValue(key,out var col) && col<row.Count
                ? row[col].Trim() : "";
            string Column(string key) => mapping.TryGetValue(key,out var col)
                ? $"{table.Headers[col]} ({col+1})" : key;
            string schoolCode = entity==ImportEntity.Schools ? Value("code") : Value("school_code");
            if(entity!=ImportEntity.Schools && schoolCode.Length==0 && defaultSchoolId is not null)
                schoolCode=context.Schools.Values.FirstOrDefault(x=>x.Id==defaultSchoolId)?.Fields["code"]??"";
            string keyCode=entity switch {
                ImportEntity.Schools => Value("code"),
                ImportEntity.Students => Value("student_code"),
                _ => Value("employee_code")
            };
            string displayKey=entity==ImportEntity.Schools ? keyCode : schoolCode+"/"+keyCode;
            ImportRowStatus result=ImportRowStatus.Invalid;
            string? entityId=null;
            var changes=new List<ImportFieldChange>();
            string message="";
            string existingRecordSha256="";
            try
            {
                if(row.Count>table.Headers.Count)
                    throw new ArgumentException("عدد الخلايا أكبر من العناوين.");
                foreach(var (field,col) in mapping)
                {
                    string text=Value(field).TrimStart();
                    if(text.Length>0 && (text[0] is '=' or '+' or '-' or '@' ||
                        text.Any(char.IsControl)))
                        throw new ArgumentException($"العمود {Column(field)}: قيمة غير آمنة أو قد تفسر كصيغة.");
                }
                if(keyCode.Length==0)
                    throw new ArgumentException($"العمود {Column(entity==ImportEntity.Schools?"code":entity==ImportEntity.Staff?"employee_code":"student_code")}: المعرف مطلوب.");
                if(entity==ImportEntity.Students)
                {
                    try { _=StudentCode(keyCode); }
                    catch(ArgumentException ex)
                    {
                        throw new ArgumentException($"العمود {Column("student_code")}: {ex.Message}");
                    }
                }
                if(schoolCode.Length==0 || !context.Schools.TryGetValue(schoolCode,out var school))
                {
                    if (entity==ImportEntity.Schools)
                    {
                        // New school code is expected. It has no existing school record.
                        school=null;
                    }
                    else if(actor.Role is Roles.Entry or Roles.Principal)
                        throw new UnauthorizedAccessException("الملف يحتوي مدرسة خارج نطاق صلاحياتك.");
                    else throw new ArgumentException($"العمود {Column("school_code")}: رمز المدرسة غير معروف.");
                }
                if(entity!=ImportEntity.Schools && school is not null)
                {
                    if(school.Fields["is_active"]!="1")
                        throw new ArgumentException("المدرسة المستهدفة مؤرشفة.");
                    if(entity==ImportEntity.Students && !AccessPolicy.CanManageStudents(actor,school.Id) ||
                       entity==ImportEntity.Staff && !AccessPolicy.CanEditStaff(actor,school.Id))
                        throw new UnauthorizedAccessException("الملف يحتوي مدرسة خارج صلاحيات التحرير.");
                }
                var lookupKey=entity==ImportEntity.Schools ? keyCode :
                    ImportKey(school!.Id,keyCode);
                var existing=entity switch
                {
                    ImportEntity.Schools=>context.Schools.GetValueOrDefault(lookupKey),
                    ImportEntity.Students=>context.Students.GetValueOrDefault(lookupKey),
                    _=>context.Staff.GetValueOrDefault(lookupKey)
                };
                entityId=existing?.Id;
                existingRecordSha256=existing is null ? "" :
                    Digest(JsonSerializer.Serialize(existing.Fields
                        .OrderBy(field=>field.Key,StringComparer.Ordinal)
                        .Select(field=>new { field.Key,field.Value }).ToArray()));
                var state=existing is null ? new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string,string>(existing.Fields,StringComparer.OrdinalIgnoreCase);
                if(entity!=ImportEntity.Schools)
                {
                    state["school_id"]=school!.Id;
                    state["school_code"]=schoolCode;
                }
                else state["code"]=existing?.Fields["code"] ?? keyCode;
                if(entity==ImportEntity.Students) state["student_code"]=keyCode;
                if(entity==ImportEntity.Staff) state["employee_code"]=keyCode;
                foreach(var field in ImportColumnMapping.Fields(entity))
                {
                    if(field is "code" or "student_code" or "employee_code" or "school_code") continue;
                    var supplied=Value(field);
                    if(supplied.Length==0) continue; // blanks never wipe existing values
                    if(field=="grade")
                    {
                        if(!GradeNames.TryParse(supplied,out var parsed))
                            throw new ArgumentException($"العمود {Column(field)}: الصف غير معروف.");
                        supplied=parsed.ToString(CultureInfo.InvariantCulture);
                    }
                    if(!state.TryGetValue(field,out var old) || old!=supplied)
                    {
                        changes.Add(new ImportFieldChange(field,old??"",supplied));
                        state[field]=supplied;
                    }
                }
                if(existing is null)
                {
                    state["is_active"]="1";
                    if(entity==ImportEntity.Schools) state.TryAdd("gender","مختلطة");
                    if(entity==ImportEntity.Staff) state.TryAdd("gender","غير محدد");
                    if(entity==ImportEntity.Students)
                    {
                        state.TryAdd("section","أ");
                        state.TryAdd("guardian_name","");
                        state.TryAdd("guardian_phone","");
                        state.TryAdd("notes","");
                        state["origin_school_id"]=school!.Id;
                        state["has_current"]="1";
                        if(!state.ContainsKey("gender") && school!.Fields["gender"]!="مختلطة")
                            state["gender"]=school.Fields["gender"];
                    }
                }
                string Required(string field)
                {
                    if(!state.TryGetValue(field,out var value) || string.IsNullOrWhiteSpace(value))
                        throw new ArgumentException($"العمود {Column(field)}: قيمة مطلوبة عند الصف الجديد.");
                    return value;
                }
                if(entity==ImportEntity.Schools)
                {
                    var code=Required("code");
                    var name=Required("name");
                    var zone=Required("zone");
                    var ownership=Required("ownership");
                    var stage=Required("stage");
                    var gender=Required("gender");
                    if(code.Length>32)
                        throw new ArgumentException($"العمود {Column("code")}: الرمز يتجاوز 32 حرفا.");
                    if(name.Length>180)
                        throw new ArgumentException($"العمود {Column("name")}: الاسم يتجاوز 180 حرفا.");
                    if(zone.Length>100)
                        throw new ArgumentException($"العمود {Column("zone")}: الموقع يتجاوز 100 حرف.");
                    if(ownership is not ("حكومية" or "أهلية"))
                        throw new ArgumentException($"العمود {Column("ownership")}: نوع الملكية يجب أن يكون حكومية أو أهلية.");
                    if(stage is not ("روضة" or "أساسي" or "ثانوي" or "أساسي وثانوي" or "مجمع تربوي"))
                        throw new ArgumentException($"العمود {Column("stage")}: المرحلة الدراسية غير معروفة.");
                    if(gender is not ("ذكور" or "إناث" or "مختلطة"))
                        throw new ArgumentException($"العمود {Column("gender")}: نوع المدرسة غير معروف.");
                    CheckSchoolInput(code,name,zone,ownership,stage);
                    CheckGender(gender);
                }
                else if(entity==ImportEntity.Staff)
                {
                    var code=Required("employee_code");
                    var name=Required("full_name");
                    var category=Required("category");
                    var gender=Required("gender");
                    if(code.Length>40)
                        throw new ArgumentException($"العمود {Column("employee_code")}: الرقم الوظيفي يتجاوز 40 حرفا.");
                    if(name.Length>180)
                        throw new ArgumentException($"العمود {Column("full_name")}: الاسم يتجاوز 180 حرفا.");
                    if(category is not ("معلم" or "إداري" or "أخصائي اجتماعي"))
                        throw new ArgumentException($"العمود {Column("category")}: فئة الموظف غير مدعومة.");
                    if(gender is not ("ذكور" or "إناث" or "غير محدد"))
                        throw new ArgumentException($"العمود {Column("gender")}: الجنس غير معروف.");
                    foreach(var field in new[]{"job_title","specialization","qualification"})
                        if(state.GetValueOrDefault(field,"").Length>120)
                            throw new ArgumentException($"العمود {Column(field)}: القيمة تتجاوز 120 حرفا.");
                    ValidateStaff(new SchoolStaff(entityId??"",school!.Id,code,name,category,
                        state.GetValueOrDefault("job_title",""),
                        state.GetValueOrDefault("specialization",""),
                        state.GetValueOrDefault("qualification",""),gender,true));
                }
                else
                {
                    var grade=Required("grade");
                    if(!int.TryParse(grade,NumberStyles.None,CultureInfo.InvariantCulture,out var number) ||
                        !GradeNames.Allowed(school!.Fields["stage"]).Contains(number))
                        throw new ArgumentException($"العمود {Column("grade")}: الصف لا يتبع مرحلة المدرسة.");
                    string section;
                    try { section=SectionName(Required("section")); }
                    catch(ArgumentException ex)
                    {
                        throw new ArgumentException($"العمود {Column("section")}: {ex.Message}");
                    }
                    if(!context.Grades.ContainsKey(GradeKey(school!.Id,grade,section)))
                        throw new ArgumentException($"العمود {Column("section")}: الشعبة {section} في الصف {grade} غير مسجلة؛ أضفها أولا.");
                    var name=Required("full_name");
                    var guardian=state.GetValueOrDefault("guardian_name","");
                    var phone=state.GetValueOrDefault("guardian_phone","");
                    if(name.Length>180)
                        throw new ArgumentException($"العمود {Column("full_name")}: الاسم يتجاوز 180 حرفا.");
                    if(guardian.Length>180)
                        throw new ArgumentException($"العمود {Column("guardian_name")}: اسم ولي الأمر يتجاوز 180 حرفا.");
                    if(phone.Length>30 || phone.Any(ch=>!(char.IsAsciiDigit(ch) || ch is '+' or '-' or ' ')))
                        throw new ArgumentException($"العمود {Column("guardian_phone")}: هاتف ولي الأمر غير صحيح.");
                    ValidateStudent(name,guardian,phone,state.GetValueOrDefault("notes",""));
                    var gender=Required("gender");
                    if(gender is not ("ذكور" or "إناث") ||
                        school.Fields["gender"]!="مختلطة" && gender!=school.Fields["gender"])
                        throw new ArgumentException($"العمود {Column("gender")}: الجنس لا يناسب المدرسة.");
                }
                if(existing is not null && existing.Fields.GetValueOrDefault("is_active")=="0")
                {
                    result=ImportRowStatus.Conflict;
                    message="السجل مؤرشف؛ لا يمكن للاستيراد إعادة تنشيطه دون عملية استعادة مستقلة.";
                }
                else if(entity==ImportEntity.Students && existing is not null &&
                    (existing.Fields.GetValueOrDefault("has_current")!="1" ||
                     existing.Fields.GetValueOrDefault("origin_school_id")!=school!.Id))
                {
                    result=ImportRowStatus.Conflict;
                    message="هوية الطالب مرتبطة بقيد سابق أو مدرسة أخرى؛ استخدم إجراءات النقل والترفيع.";
                }
                else if(entity==ImportEntity.Students && existing is not null &&
                    changes.Any(change=>change.Field is "grade" or "section"))
                {
                    result=ImportRowStatus.Conflict;
                    message="الصف أو الشعبة تغيرت: يجب استخدام نقل الشعبة أو الترفيع الموثق، وليس تعديل القيد التاريخي عبر الاستيراد.";
                }
                else if(entity==ImportEntity.Staff &&
                    context.StaffOwners.TryGetValue(keyCode,out var owners) &&
                    owners.Any(x=>x!=school!.Id))
                {
                    result=ImportRowStatus.Conflict;
                    message="الرقم الوظيفي مرتبط بملاك مدرسة أخرى؛ سجّل تكليفا بدلا من نسخ هوية الموظف.";
                }
                else
                {
                    result=existing is null ? ImportRowStatus.New :
                        changes.Count==0 ? ImportRowStatus.Unchanged : ImportRowStatus.Update;
                    message=result switch {ImportRowStatus.New=>"إضافة جديدة",
                        ImportRowStatus.Update=>"تحديث القيم الموضحة فقط",_=>"دون تغيير"};
                }
                // New-row audit: preview every populated field rather than just changes
                // generated by a column mapping. Defaults must be transparent too.
                if(existing is null && result==ImportRowStatus.New)
                    changes=state.Where(x=>x.Key is not ("school_id" or "origin_school_id" or
                        "has_current" or "is_active" or "notes"))
                        .Select(x=>new ImportFieldChange(x.Key,"",x.Value)).ToList();
            }
            catch(UnauthorizedAccessException) { throw; }
            catch(ArgumentException error) { message=$"الصف {line}: {error.Message}";result=ImportRowStatus.Invalid; }
            if(!seen.TryAdd(displayKey,index))
            {
                result=ImportRowStatus.Duplicate;
                message=$"الصف {line}: المعرف مكرر في الصف {seen[displayKey]+2}.";
                var earlier=planned[seen[displayKey]];
                planned[seen[displayKey]]=earlier with {Status=ImportRowStatus.Duplicate,
                    Message=$"الصف {earlier.RowNumber}: المعرف مكرر في الصف {line}."};
            }
            if(entity==ImportEntity.Staff && keyCode.Length>0 &&
                !seenPayrollCodes.TryAdd(keyCode,index) &&
                !string.Equals(planned[seenPayrollCodes[keyCode]].RecordKey,displayKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                var earlierIndex=seenPayrollCodes[keyCode];
                var prior=planned[earlierIndex];
                planned[earlierIndex]=prior with
                {
                    Status=ImportRowStatus.Conflict,
                    Message=$"الصف {prior.RowNumber}: الرقم الوظيفي يرد ضمن ملاك أكثر من مدرسة في الملف (الصف {line})."
                };
                result=ImportRowStatus.Conflict;
                message=$"الصف {line}: الرقم الوظيفي نفسه موجود في ملاك مدرسة أخرى بالصف {prior.RowNumber}.";
            }
            planned.Add(new ImportPlanRow(line,displayKey,result,entityId,changes,message,existingRecordSha256));
        }
        var sourceHash=ImportSourceHash(entity,sourceName,table,mapping,defaultSchoolId);
        return new ImportPreview(entity,sourceName,table,mapping,defaultSchoolId,
            context.AcademicYear,sourceHash,ImportPlanHash(sourceHash,context.AcademicYear,planned),planned);
    }
}
