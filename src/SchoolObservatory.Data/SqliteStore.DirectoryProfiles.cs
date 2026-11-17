using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    /// <summary>
    /// Add new optional school/personnel fields in separate one-to-one tables.
    /// No rebuild or overwrite of schools/school_staff or their existing relationships.
    /// </summary>
    private static void MigrateDirectoryProfiles(SqliteConnection con)
    {
        using var cmd = Cmd(con, null, """
            CREATE TABLE IF NOT EXISTS school_directory_details (
                school_id TEXT PRIMARY KEY REFERENCES schools(id) ON DELETE RESTRICT,
                principal_name TEXT NOT NULL DEFAULT '',
                contact_phone TEXT NOT NULL DEFAULT '',
                address TEXT NOT NULL DEFAULT '',
                founded_year INTEGER NULL,
                notes TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS staff_personnel_details (
                staff_id TEXT PRIMARY KEY REFERENCES school_staff(id) ON DELETE RESTRICT,
                employment_type TEXT NOT NULL DEFAULT 'غير محدد',
                employment_year INTEGER NULL,
                contract_year INTEGER NULL,
                qualification_year INTEGER NULL,
                assignment_status TEXT NOT NULL DEFAULT 'غير محدد',
                actual_workplace TEXT NOT NULL DEFAULT '',
                assignment_year INTEGER NULL,
                contact_phone TEXT NOT NULL DEFAULT '',
                notes TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL
            );
            """);
        cmd.ExecuteNonQuery();
    }

    private static int? OptionalYear(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetInt32(i);

    private static int? SafeYear(int? year, string label)
    {
        if (year is not null && year is < 1900 or > 2100)
            throw new ArgumentException($"سنة {label} يجب أن تكون بين 1900 و2100.");
        return year;
    }

    private static void ValidateSchoolDetails(SchoolDirectoryDetails details)
    {
        if (details.PrincipalName.Length > 180 || details.ContactPhone.Length > 40 ||
            details.Address.Length > 300 || details.Notes.Length > 2500)
            throw new ArgumentException("بيانات ملف المدرسة تتجاوز أطوال الحقول المسموحة.");
        SafeYear(details.FoundedYear, "التأسيس");
    }

    private static void ValidatePersonnel(StaffPersonnelDetails details)
    {
        if (details.EmploymentType is not ("غير محدد" or "موظف" or "متعاقد") ||
            details.AssignmentStatus is not ("غير محدد" or "مباشر" or "منتدب" or "مكلف" or "معار" or "إجازة"))
            throw new ArgumentException("نوع التوظيف أو وضع العمل غير صحيح.");
        SafeYear(details.EmploymentYear, "التوظيف");
        SafeYear(details.ContractYear, "التعاقد");
        SafeYear(details.QualificationYear, "الحصول على المؤهل");
        SafeYear(details.AssignmentYear, "بداية التكليف أو الانتداب");
        if ((details.EmploymentType == "موظف" &&
                (details.EmploymentYear is null || details.ContractYear is not null)) ||
            (details.EmploymentType == "متعاقد" &&
                (details.ContractYear is null || details.EmploymentYear is not null)) ||
            (details.EmploymentType == "غير محدد" &&
                (details.EmploymentYear is not null || details.ContractYear is not null)))
            throw new ArgumentException(
                "اختر موظف مع سنة التوظيف، أو متعاقد مع سنة التعاقد، دون الجمع بينهما.");
        if (details.AssignmentStatus is ("منتدب" or "مكلف" or "معار") &&
            string.IsNullOrWhiteSpace(details.ActualWorkplace))
            throw new ArgumentException("حدد جهة العمل الفعلية عند الانتداب أو التكليف أو الإعارة.");
        if (details.ActualWorkplace.Length > 250 || details.ContactPhone.Length > 40 ||
            details.Notes.Length > 2500)
            throw new ArgumentException("تفاصيل ملف الكادر تتجاوز أطوال الحقول المسموحة.");
    }

    public SchoolDirectoryDetails GetSchoolDirectoryDetails(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeSchool(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية الاطلاع على ملف هذه المدرسة.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT d.principal_name,d.contact_phone,d.address,d.founded_year,d.notes
            FROM schools s LEFT JOIN school_directory_details d ON d.school_id=s.id
            WHERE s.id=$school
            """, ("$school", schoolId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new UnauthorizedAccessException("المدرسة غير متاحة لهذا الحساب.");
        return new SchoolDirectoryDetails(
            r.IsDBNull(0) ? "" : Text(r, 0),
            r.IsDBNull(1) ? "" : Text(r, 1),
            r.IsDBNull(2) ? "" : Text(r, 2),
            OptionalYear(r, 3),
            r.IsDBNull(4) ? "" : Text(r, 4));
    }

    public void SaveSchoolDirectoryDetails(UserAccount actor, string schoolId,
        SchoolDirectoryDetails details)
    {
        RequireFreshActor(actor);
        if (!(AccessPolicy.CanEditSchool(actor) && AccessPolicy.CanSeeSchool(actor, schoolId)) &&
            !AccessPolicy.CanEditSchoolProfile(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية تعديل ملف المدرسة.");
        ValidateSchoolDetails(details);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = Cmd(con, tx, """
            INSERT INTO school_directory_details(school_id,principal_name,contact_phone,
                address,founded_year,notes,updated_utc)
            SELECT s.id,$principal,$phone,$address,$year,$notes,$at FROM schools s
            WHERE s.id=$school
            ON CONFLICT(school_id) DO UPDATE SET
                principal_name=excluded.principal_name,
                contact_phone=excluded.contact_phone,
                address=excluded.address,
                founded_year=excluded.founded_year,
                notes=excluded.notes,
                updated_utc=excluded.updated_utc
            """, ("$school", schoolId), ("$principal", details.PrincipalName.Trim()),
            ("$phone", details.ContactPhone.Trim()), ("$address", details.Address.Trim()),
            ("$year", details.FoundedYear), ("$notes", details.Notes.Trim()), ("$at", Utc()));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("المدرسة غير موجودة.");
        Audit(con, tx, actor.Id, "school.details_update", schoolId, "school profile fields updated");
        tx.Commit();
    }

    /// <summary>Fetch one member by school and identifier; never return foreign-school data.</summary>
    public SchoolStaff GetSchoolStaffMember(UserAccount actor, string schoolId, string staffId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStaff(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض هذا الكادر.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT id,school_id,employee_code,full_name,category,job_title,
                specialization,qualification,gender,is_active
            FROM school_staff WHERE id=$id AND school_id=$school
            """, ("$id", staffId), ("$school", schoolId));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            throw new UnauthorizedAccessException("سجل الموظف غير متاح ضمن هذه المدرسة.");
        return ReadStaff(r);
    }

    public StaffPersonnelDetails GetStaffPersonnel(UserAccount actor, string schoolId, string staffId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStaff(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية عرض ملف الموظف.");
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT d.employment_type,d.employment_year,d.contract_year,d.qualification_year,
                d.assignment_status,d.actual_workplace,d.assignment_year,d.contact_phone,d.notes
            FROM school_staff s
            LEFT JOIN staff_personnel_details d ON d.staff_id=s.id
            WHERE s.id=$id AND s.school_id=$school
            """, ("$id", staffId), ("$school", schoolId));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            throw new UnauthorizedAccessException("ملف الموظف غير متاح ضمن هذه المدرسة.");
        return new StaffPersonnelDetails(
            r.IsDBNull(0) ? "غير محدد" : Text(r, 0),
            OptionalYear(r, 1), OptionalYear(r, 2), OptionalYear(r, 3),
            r.IsDBNull(4) ? "غير محدد" : Text(r, 4),
            r.IsDBNull(5) ? "" : Text(r, 5), OptionalYear(r, 6),
            r.IsDBNull(7) ? "" : Text(r, 7),
            r.IsDBNull(8) ? "" : Text(r, 8));
    }

    public void SaveStaffPersonnel(UserAccount actor, string schoolId,
        string staffId, StaffPersonnelDetails details)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditStaff(actor, schoolId))
            throw new UnauthorizedAccessException("لا تملك صلاحية تعديل بيانات هذا الموظف.");
        ValidatePersonnel(details);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = Cmd(con, tx, """
            INSERT INTO staff_personnel_details(staff_id,employment_type,employment_year,
                contract_year,qualification_year,assignment_status,actual_workplace,
                assignment_year,contact_phone,notes,updated_utc)
            SELECT s.id,$type,$employment,$contract,$qualification,$status,$place,
                $assignment,$phone,$notes,$at
            FROM school_staff s WHERE s.id=$id AND s.school_id=$school
            ON CONFLICT(staff_id) DO UPDATE SET
                employment_type=excluded.employment_type,
                employment_year=excluded.employment_year,
                contract_year=excluded.contract_year,
                qualification_year=excluded.qualification_year,
                assignment_status=excluded.assignment_status,
                actual_workplace=excluded.actual_workplace,
                assignment_year=excluded.assignment_year,
                contact_phone=excluded.contact_phone,
                notes=excluded.notes,
                updated_utc=excluded.updated_utc
            """, ("$id", staffId), ("$school", schoolId),
            ("$type", details.EmploymentType),
            ("$employment", details.EmploymentYear), ("$contract", details.ContractYear),
            ("$qualification", details.QualificationYear),
            ("$status", details.AssignmentStatus), ("$place", details.ActualWorkplace.Trim()),
            ("$assignment", details.AssignmentYear), ("$phone", details.ContactPhone.Trim()),
            ("$notes", details.Notes.Trim()), ("$at", Utc()));
        if (cmd.ExecuteNonQuery() != 1)
            throw new UnauthorizedAccessException("لم يعثر على الموظف ضمن المدرسة المحددة.");
        Audit(con, tx, actor.Id, "staff.personnel_update", staffId,
            $"school={schoolId};employment={details.EmploymentType};assignment={details.AssignmentStatus}");
        tx.Commit();
    }
}
