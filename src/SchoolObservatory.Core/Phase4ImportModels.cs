namespace SchoolObservatory.Core;

/// <summary>Only these supported domain records can be imported. All other data stays read-only.</summary>
public enum ImportEntity { Schools, Students, Staff }

public enum ImportRowStatus { New, Update, Unchanged, Duplicate, Conflict, Invalid }

public sealed record ImportTable(
    string Format, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record ImportFieldChange(string Field, string Before, string After);

public sealed record ImportPlanRow(
    int RowNumber, string RecordKey, ImportRowStatus Status, string? EntityId,
    IReadOnlyList<ImportFieldChange> Changes, string Message);

/// <summary>Contains the exact immutable source values the user saw at preview time.</summary>
public sealed record ImportPreview(
    ImportEntity Entity, string SourceName, ImportTable Table,
    IReadOnlyDictionary<string,int> ColumnMap, string? DefaultSchoolId,
    string AcademicYear, string SourceSha256, string StateSha256,
    IReadOnlyList<ImportPlanRow> Rows)
{
    public int Created => Rows.Count(x => x.Status == ImportRowStatus.New);
    public int Updated => Rows.Count(x => x.Status == ImportRowStatus.Update);
    public int Unchanged => Rows.Count(x => x.Status == ImportRowStatus.Unchanged);
    public int Rejected => Rows.Count(x => x.Status is ImportRowStatus.Invalid or ImportRowStatus.Conflict or ImportRowStatus.Duplicate);
    public bool CanCommit => Rows.Count > 0 && Rejected == 0;
}

public sealed record ImportBatchSummary(
    string Id, ImportEntity Entity, string SourceName, string AcademicYear,
    string ImportedUtc, string Username, int Created, int Updated, int Unchanged,
    int Rejected, string Status, string Warnings);

public static class ImportColumnMapping
{
    private static readonly IReadOnlyDictionary<ImportEntity, Dictionary<string,string[]>> Schemas =
        new Dictionary<ImportEntity, Dictionary<string,string[]>>
        {
            [ImportEntity.Schools] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["code"] = ["code", "رمز المدرسة", "كود المدرسة"],
                ["name"] = ["name", "اسم المدرسة"],
                ["zone"] = ["zone", "المنطقة"],
                ["ownership"] = ["ownership", "الملكية", "نوع الملكية"],
                ["stage"] = ["stage", "المرحلة"],
                ["gender"] = ["gender", "الجنس", "نوع المدرسة"]
            },
            [ImportEntity.Students] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["school_code"] = ["school_code", "رمز المدرسة"],
                ["student_code"] = ["student_code", "رمز الطالب", "الرقم المدرسي", "رقم الطالب"],
                ["full_name"] = ["full_name", "الاسم", "اسم الطالب", "اسم الطالبة", "الاسم الكامل"],
                ["grade"] = ["grade", "الصف", "الصف الدراسي"],
                ["section"] = ["section", "الشعبة", "الفصل"],
                ["gender"] = ["gender", "الجنس"],
                ["guardian_name"] = ["guardian_name", "ولي الأمر", "اسم ولي الأمر"],
                ["guardian_phone"] = ["guardian_phone", "هاتف ولي الأمر", "رقم هاتف ولي الأمر"]
            },
            [ImportEntity.Staff] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["school_code"] = ["school_code", "رمز المدرسة"],
                ["employee_code"] = ["employee_code", "الرقم الوظيفي", "رقم الموظف"],
                ["full_name"] = ["full_name", "الاسم", "اسم الموظف", "الاسم الكامل"],
                ["category"] = ["category", "الفئة", "فئة الموظف"],
                ["job_title"] = ["job_title", "المسمى الوظيفي"],
                ["specialization"] = ["specialization", "التخصص"],
                ["qualification"] = ["qualification", "المؤهل"],
                ["gender"] = ["gender", "الجنس"]
            }
        };

    public static IReadOnlyList<string> Fields(ImportEntity entity) =>
        Schemas[entity].Keys.ToArray();

    public static IReadOnlyDictionary<string,int> Auto(ImportEntity entity, IReadOnlyList<string> headers)
    {
        if (headers.Count == 0 || headers.Count > 30)
            throw new ArgumentException("العناوين مفقودة أو الأعمدة تتجاوز 30.");
        var mapping = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for (var i=0; i<headers.Count; i++)
        {
            string normalized = headers[i].Trim().TrimStart('\uFEFF');
            foreach (var (field, aliases) in Schemas[entity])
                if (aliases.Any(a => string.Equals(a, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!mapping.TryAdd(field, i))
                        throw new ArgumentException($"عنوان الحقل {field} مكرر في العمود {i+1}.");
                    break;
                }
        }
        if (!mapping.ContainsKey(entity switch
            { ImportEntity.Schools => "code", ImportEntity.Students => "student_code", _ => "employee_code" }))
            throw new ArgumentException("الملف لا يحتوي عنوان المعرف الأساسي المطلوب (رمز المدرسة/الطالب/الموظف).");
        return mapping;
    }

    public static void Validate(ImportEntity entity, IReadOnlyList<string> headers,
        IReadOnlyDictionary<string,int> mapping)
    {
        if (headers.Count is 0 or > 30) throw new ArgumentException("عدد أعمدة غير صحيح.");
        var used = new HashSet<int>();
        foreach (var (key,index) in mapping)
        {
            if (!Schemas[entity].ContainsKey(key) || index < 0 || index >= headers.Count || !used.Add(index))
                throw new ArgumentException($"خريطة الأعمدة غير صالحة عند الحقل {key}.");
        }
        if (!mapping.ContainsKey(entity == ImportEntity.Schools ? "code" :
            entity == ImportEntity.Students ? "student_code" : "employee_code"))
            throw new ArgumentException("ربط عمود المعرف الأساسي مطلوب قبل الاستيراد.");
    }
}
