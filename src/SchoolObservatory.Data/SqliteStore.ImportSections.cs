using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    /// <summary>Extra Phase 4 academic import: only registered grade/section groups, never rooms.</summary>
    private ImportPreview BuildSectionsPreview(SqliteConnection con,SqliteTransaction tx,
        UserAccount actor,string sourceName,ImportTable table,
        IReadOnlyDictionary<string,int> mapping,string? defaultSchoolId)
    {
        var state=LoadImportContext(con,tx);
        RequireOpenYear(con,tx,state.AcademicYear);
        var rows=new List<ImportPlanRow>();
        var seen=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for(var i=0;i<table.Rows.Count;i++)
        {
            var values=table.Rows[i];
            var rowNumber=i+2;
            var schoolCode=LookupInput(values,mapping,"school_code");
            var gradeText=LookupInput(values,mapping,"grade");
            var section=LookupInput(values,mapping,"section");
            if(schoolCode.Length==0 && defaultSchoolId is not null)
                schoolCode=state.Schools.Values.FirstOrDefault(x=>x.Id==defaultSchoolId)?.Fields["code"]??"";
            var key=schoolCode+"/"+gradeText+"/"+section;
            var status=ImportRowStatus.Invalid;
            string? id=null;
            string message="";
            var changes=new List<ImportFieldChange>();
            try
            {
                if(values.Count>table.Headers.Count)
                    throw new ArgumentException("عدد الخلايا يتجاوز العناوين.");
                foreach(var (field,column) in mapping)
                {
                    var value=LookupInput(values,mapping,field);
                    if(value.Length>0 && (value[0] is '=' or '+' or '-' or '@' ||
                        value.Any(char.IsControl)))
                        throw new ArgumentException($"العمود {table.Headers[column]}: صيغة محتملة أو محرف غير آمن.");
                }
                if(!state.Schools.TryGetValue(schoolCode,out var school))
                {
                    if(actor.Role is Roles.Entry or Roles.Principal)
                        throw new UnauthorizedAccessException("رمز المدرسة خارج نطاق صلاحياتك.");
                    throw new ArgumentException("العمود رمز المدرسة: لم يتم العثور على مدرسة.");
                }
                if(!AccessPolicy.CanManageStudents(actor,school.Id))
                    throw new UnauthorizedAccessException("لا تملك صلاحية تعديل صفوف هذه المدرسة.");
                if(school.Fields["is_active"]!="1")
                    throw new ArgumentException("المدرسة مؤرشفة.");
                if(!GradeNames.TryParse(gradeText,out var grade) ||
                    !GradeNames.Allowed(school.Fields["stage"]).Contains(grade))
                    throw new ArgumentException("العمود الصف: الصف غير صحيح لمرحلة المدرسة.");
                if(string.IsNullOrWhiteSpace(section) || section.Length>25)
                    throw new ArgumentException("العمود الشعبة: يجب تحديد شعبة صحيحة.");
                gradeText=grade.ToString(CultureInfo.InvariantCulture);
                key=schoolCode+"/"+gradeText+"/"+section;
                status=state.Grades.TryGetValue(GradeKey(school.Id,gradeText,section),out id)
                    ? ImportRowStatus.Unchanged:ImportRowStatus.New;
                message=status==ImportRowStatus.New?"إضافة شعبة جديدة للعام النشط":"الشعبة مسجلة سابقا دون تغيير.";
                if(status==ImportRowStatus.New)
                {
                    changes.Add(new ImportFieldChange("school_code","",schoolCode));
                    changes.Add(new ImportFieldChange("grade","",gradeText));
                    changes.Add(new ImportFieldChange("section","",section));
                }
            }
            catch(UnauthorizedAccessException) { throw; }
            catch(ArgumentException ex)
            {
                status=ImportRowStatus.Invalid;
                message=$"الصف {rowNumber}: {ex.Message}";
            }
            if(!seen.TryAdd(key,i))
            {
                status=ImportRowStatus.Duplicate;
                message=$"الصف {rowNumber}: شعبة مكررة بالصف {seen[key]+2}.";
                var prior=rows[seen[key]];
                rows[seen[key]]=prior with {Status=ImportRowStatus.Duplicate,
                    Message=$"الصف {prior.RowNumber}: شعبة مكررة بالصف {rowNumber}."};
            }
            rows.Add(new ImportPlanRow(rowNumber,key,status,id,changes,message));
        }
        var sourceHash=ImportSourceHash(ImportEntity.Sections,sourceName,table,mapping,defaultSchoolId);
        return new ImportPreview(ImportEntity.Sections,sourceName,table,mapping,defaultSchoolId,
            state.AcademicYear,sourceHash,ImportPlanHash(sourceHash,state.AcademicYear,rows),rows);
    }
}
