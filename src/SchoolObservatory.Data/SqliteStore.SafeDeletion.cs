using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

/// <summary>Deliberately narrow permanent deletion: never hard-delete students or employees.</summary>
public sealed partial class SqliteStore
{
    public void DeleteEmptySchool(UserAccount actor,string schoolId,string confirmationCode)
    {
        RequireFreshActor(actor);
        if(!actor.IsOwner || !AccessPolicy.CanEditSchool(actor))
            throw new UnauthorizedAccessException("الحذف النهائي للمدارس غير المستخدمة للمسؤول المؤسس فقط.");
        if(string.IsNullOrWhiteSpace(schoolId))
            throw new ArgumentException("حدد مدرسة.");
        using var con=Open();
        using var tx=con.BeginTransaction(deferred:false);
        string code;
        int active;
        using(var school=Cmd(con,tx,"SELECT code,is_active FROM schools WHERE id=$id",
            ("$id",schoolId)))
        using(var r=school.ExecuteReader())
        {
            if(!r.Read())throw new ArgumentException("المدرسة غير موجودة.");
            code=Text(r,0);active=r.GetInt32(1);
        }
        if(!string.Equals(code,confirmationCode,StringComparison.Ordinal))
            throw new ArgumentException("رمز تأكيد المدرسة لا يطابق الرمز المسجل حرفيا.");
        if(active!=0)
            throw new InvalidOperationException("أرشف المدرسة واحفظ الأرشفة أولا، ثم اطلب الحذف النهائي.");

        using(var refs=Cmd(con,tx,"""
            SELECT
                (SELECT COUNT(*) FROM users WHERE school_id=$id)
              + (SELECT COUNT(*) FROM submissions WHERE school_id=$id)
              + (SELECT COUNT(*) FROM school_grades WHERE school_id=$id)
              + (SELECT COUNT(*) FROM school_students WHERE school_id=$id)
              + (SELECT COUNT(*) FROM school_staff WHERE school_id=$id)
              + (SELECT COUNT(*) FROM student_history WHERE school_id=$id)
              + (SELECT COUNT(*) FROM employee_assignments
                 WHERE payroll_school_id=$id OR workplace_school_id=$id)
              + (SELECT COUNT(*) FROM import_changes WHERE school_id=$id)
            """,("$id",schoolId)))
            if(Convert.ToInt64(refs.ExecuteScalar(),CultureInfo.InvariantCulture)!=0)
                throw new InvalidOperationException(
                    "المدرسة مرتبطة بمستخدمين أو طلاب أو موظفين أو شعب أو اعتماد رسمي أو دفعة استيراد؛ لا يجوز حذفها. أبقها مؤرشفة.");
        try
        {
            using var del=Cmd(con,tx,"DELETE FROM schools WHERE id=$id AND is_active=0",
                ("$id",schoolId));
            if(del.ExecuteNonQuery()!=1)
                throw new InvalidOperationException("تغيرت حالة المدرسة؛ أعِد الفحص.");
        }
        catch(SqliteException ex)
        {
            throw new InvalidOperationException(
                "لا يمكن حذف هذه المدرسة لأن بها ملفات متصلة أو بيانات تاريخية؛ استخدم الأرشفة.",ex);
        }
        Audit(con,tx,actor.Id,"school.delete.empty",schoolId,$"confirmed_code={code}");
        tx.Commit();
    }
}
