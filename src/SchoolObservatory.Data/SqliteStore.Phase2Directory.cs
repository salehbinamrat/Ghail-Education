using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    public SchoolInfrastructure GetSchoolInfrastructure(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeSchool(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT district,subdistrict,village,address_details,latitude,longitude,
                   building_type,building_condition,classrooms_count,operating_shifts
            FROM school_infrastructure WHERE school_id=$s
            """, ("$s", schoolId));
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new SchoolInfrastructure(reader.GetString(0),reader.GetString(1),reader.GetString(2),
                reader.GetString(3),reader.IsDBNull(4)?null:reader.GetDouble(4),
                reader.IsDBNull(5)?null:reader.GetDouble(5),reader.GetString(6),
                reader.GetString(7),reader.IsDBNull(8)?null:reader.GetInt32(8),reader.GetString(9))
            : new SchoolInfrastructure("","","","",null,null,"","",null,"");
    }

    public void SaveSchoolInfrastructure(UserAccount actor, string schoolId, SchoolInfrastructure value)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditSchoolProfile(actor, schoolId)) throw new UnauthorizedAccessException();
        if (value.ClassroomsCount is < 0 || value.Latitude is < -90 or > 90 ||
            value.Longitude is < -180 or > 180)
            throw new ArgumentException("Invalid school infrastructure values.");
        foreach (var field in new[] { value.District, value.Subdistrict, value.Village,
            value.AddressDetails, value.BuildingType, value.BuildingCondition, value.OperatingShifts })
            if (field.Length > 300) throw new ArgumentException("School profile field too long.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var cmd = Cmd(con, tx, """
            INSERT INTO school_infrastructure(school_id,district,subdistrict,village,address_details,
              latitude,longitude,building_type,building_condition,classrooms_count,operating_shifts,updated_utc)
            VALUES($s,$district,$sub,$village,$address,$lat,$lon,$building,$condition,$rooms,$shifts,$at)
            ON CONFLICT(school_id) DO UPDATE SET
              district=excluded.district,subdistrict=excluded.subdistrict,village=excluded.village,
              address_details=excluded.address_details,latitude=excluded.latitude,
              longitude=excluded.longitude,building_type=excluded.building_type,
              building_condition=excluded.building_condition,classrooms_count=excluded.classrooms_count,
              operating_shifts=excluded.operating_shifts,updated_utc=excluded.updated_utc
            """, ("$s", schoolId), ("$district", value.District), ("$sub", value.Subdistrict),
            ("$village", value.Village), ("$address", value.AddressDetails),
            ("$lat", value.Latitude), ("$lon", value.Longitude),
            ("$building", value.BuildingType), ("$condition", value.BuildingCondition),
            ("$rooms", value.ClassroomsCount), ("$shifts", value.OperatingShifts),
            ("$at", Utc()))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "school.infrastructure", schoolId, "");
        tx.Commit();
    }

    public IReadOnlyList<SchoolFacility> GetSchoolFacilities(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeSchool(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null,
            "SELECT facility_type,quantity,notes FROM school_facilities WHERE school_id=$s ORDER BY facility_type",
            ("$s", schoolId));
        using var reader = cmd.ExecuteReader();
        var list = new List<SchoolFacility>();
        while (reader.Read()) list.Add(new(reader.GetString(0),reader.GetInt32(1),reader.GetString(2)));
        return list;
    }

    public void SetSchoolFacility(UserAccount actor, string schoolId, string type, int quantity, string notes)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditSchoolProfile(actor, schoolId)) throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(type) || type.Length > 100 || quantity < 0 || notes.Length > 1000)
            throw new ArgumentException("Invalid facility details.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var cmd = Cmd(con, tx, """
            INSERT INTO school_facilities(school_id,facility_type,quantity,notes)
            VALUES($s,$type,$quantity,$notes)
            ON CONFLICT(school_id,facility_type) DO UPDATE SET
              quantity=excluded.quantity,notes=excluded.notes
            """, ("$s", schoolId), ("$type", type.Trim()),
            ("$quantity", quantity), ("$notes", notes))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "school.facility", schoolId, type);
        tx.Commit();
    }

    public string AddPhysicalClassroom(UserAccount actor, string schoolId,
        string roomCode, string building, int? capacity)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudents(actor, schoolId)) throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(roomCode) || roomCode.Length > 50 ||
            building.Length > 150 || capacity is < 0)
            throw new ArgumentException("Invalid physical classroom.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        var id=Id();
        using (var cmd = Cmd(con, tx, """
            INSERT INTO school_rooms(id,school_id,room_code,building,capacity)
            VALUES($id,$s,$code,$building,$capacity)
            """, ("$id", id), ("$s", schoolId), ("$code", roomCode.Trim()),
            ("$building", building), ("$capacity", capacity))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "school.room.create", id, schoolId);
        tx.Commit();
        return id;
    }

    public void AssignSectionRoom(UserAccount actor, string schoolId, string gradeId, string? roomId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanManageStudents(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var grade = Cmd(con, tx,
            "SELECT academic_year FROM school_grades WHERE id=$id AND school_id=$s",
            ("$id", gradeId), ("$s", schoolId)))
        {
            var year=grade.ExecuteScalar()?.ToString();
            if (year is null) throw new InvalidOperationException("Section does not belong to school.");
            using var closed=Cmd(con, tx,
                "SELECT status FROM academic_years WHERE year=$y", ("$y", year));
            if (closed.ExecuteScalar()?.ToString()!="open")
                throw new InvalidOperationException("Closed-year sections cannot be changed.");
        }
        using (var cmd = Cmd(con, tx, """
            INSERT INTO section_room_assignments(grade_id,school_id,room_id)
            VALUES($grade,$school,$room)
            ON CONFLICT(grade_id) DO UPDATE SET room_id=excluded.room_id
            """, ("$grade", gradeId), ("$school", schoolId), ("$room", roomId))) cmd.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "school.section.room", gradeId, roomId ?? "");
        tx.Commit();
    }

    public IReadOnlyList<PhysicalSectionRoom> GetSchoolSectionsWithRooms(UserAccount actor, string schoolId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStudents(actor, schoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT g.id,g.grade,g.section,r.room_code,r.building
            FROM school_grades g
            LEFT JOIN section_room_assignments a ON a.grade_id=g.id
            LEFT JOIN school_rooms r ON r.id=a.room_id AND r.school_id=g.school_id
            WHERE g.school_id=$s AND g.academic_year=$y
            ORDER BY g.grade,g.section
            """, ("$s", schoolId), ("$y", AcademicYear));
        using var reader = cmd.ExecuteReader();
        var list = new List<PhysicalSectionRoom>();
        while (reader.Read()) list.Add(new(reader.GetString(0),reader.GetInt32(1),
            reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),
            reader.IsDBNull(4)?null:reader.GetString(4)));
        return list;
    }

    public IReadOnlyList<EmployeeAssignment> GetEmployeeAssignments(UserAccount actor,
        string payrollSchoolId, string staffId)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanSeeStaff(actor, payrollSchoolId)) throw new UnauthorizedAccessException();
        using var con = Open();
        using var cmd = Cmd(con, null, """
            SELECT a.id,a.person_id,a.payroll_school_id,a.workplace_school_id,
                   a.assignment_type,a.start_date,a.end_date,a.notes
            FROM employee_assignments a JOIN employee_staff_links l ON l.person_id=a.person_id
            JOIN school_staff s ON s.id=l.staff_id
            WHERE l.staff_id=$staff AND s.school_id=$school
              AND ($district=1 OR a.payroll_school_id=$school)
            ORDER BY a.start_date
            """, ("$staff", staffId), ("$school", payrollSchoolId),
            ("$district", actor.Role is Roles.Entry or Roles.Principal ? 0 : 1));
        using var reader=cmd.ExecuteReader();
        var list=new List<EmployeeAssignment>();
        while (reader.Read()) list.Add(new(reader.GetString(0),reader.GetString(1),
            reader.GetString(2),reader.GetString(3),reader.GetString(4),
            DateOnly.ParseExact(reader.GetString(5),"yyyy-MM-dd",CultureInfo.InvariantCulture),
            reader.IsDBNull(6)?null:DateOnly.ParseExact(reader.GetString(6),"yyyy-MM-dd",CultureInfo.InvariantCulture),
            reader.GetString(7)));
        return list;
    }

    public void AssignEmployee(UserAccount actor, string payrollSchoolId, string staffId,
        string workplaceSchoolId, string assignmentType, DateOnly start, DateOnly? end, string notes)
    {
        RequireFreshActor(actor);
        if (!AccessPolicy.CanEditStaff(actor, payrollSchoolId) ||
            !AccessPolicy.CanSeeSchool(actor, workplaceSchoolId))
            throw new UnauthorizedAccessException();
        if (payrollSchoolId!=workplaceSchoolId && actor.Role is Roles.Entry or Roles.Principal)
            throw new UnauthorizedAccessException("District authorization required for cross-school assignments.");
        if (assignmentType is not ("original" or "assignment" or "secondment" or "transfer") ||
            end < start || notes.Length > 1000)
            throw new ArgumentException("Invalid employment assignment.");
        using var con=Open();
        using var tx=con.BeginTransaction();
        string personId;
        string code;
        string name;
        using (var cmd=Cmd(con,tx, """
            SELECT COALESCE(l.person_id,s.id),s.employee_code,s.full_name
            FROM school_staff s LEFT JOIN employee_staff_links l ON l.staff_id=s.id
            WHERE s.id=$id AND s.school_id=$school
            """,("$id",staffId),("$school",payrollSchoolId)))
        using (var reader=cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Employee not found at payroll school.");
            personId=reader.GetString(0);code=reader.GetString(1);name=reader.GetString(2);
        }
        using (var ensure=Cmd(con,tx, """
            INSERT OR IGNORE INTO employee_people(id,employee_code,full_name,created_utc)
            VALUES($p,$code,$name,$at);
            INSERT OR IGNORE INTO employee_staff_links(staff_id,person_id) VALUES($staff,$p);
            """,("$p",personId),("$code",code),("$name",name),("$at",Utc()),("$staff",staffId)))
            ensure.ExecuteNonQuery();
        using (var overlap=Cmd(con,tx, """
            SELECT COUNT(*) FROM employee_assignments
            WHERE person_id=$p AND (end_date IS NULL OR end_date >= $start)
              AND ($end IS NULL OR start_date <= $end)
            """,("$p",personId),("$start",start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),
            ("$end",end?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))))
            if (Convert.ToInt32(overlap.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
                throw new InvalidOperationException("Employee assignment dates overlap.");
        var id=Id();
        using (var insert=Cmd(con,tx, """
            INSERT INTO employee_assignments(id,person_id,payroll_school_id,workplace_school_id,
              assignment_type,start_date,end_date,notes,created_utc,actor_id)
            VALUES($id,$p,$payroll,$work,$type,$start,$end,$notes,$at,$actor)
            """,("$id",id),("$p",personId),("$payroll",payrollSchoolId),
            ("$work",workplaceSchoolId),("$type",assignmentType),
            ("$start",start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),
            ("$end",end?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),
            ("$notes",notes),("$at",Utc()),("$actor",actor.Id))) insert.ExecuteNonQuery();
        Audit(con,tx,actor.Id,"employee.assignment",personId,$"workplace={workplaceSchoolId};type={assignmentType}");
        tx.Commit();
    }
}
