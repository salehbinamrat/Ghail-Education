namespace SchoolObservatory.Core;

/// <summary>Authoritative academic-year lifecycle. Exactly one year may be active.</summary>
public sealed record AcademicYearRecord(string Year, bool IsActive, bool IsClosed,
    string OpenedUtc, string? ClosedUtc);

/// <summary>One immutable student identity may have many dated placements, never two active placements per year.</summary>
public sealed record StudentEnrollmentHistoryRecord(string Id, string StudentId,
    string SchoolId, string AcademicYear, int Grade, string Section, string Status,
    string StartedUtc, string? EndedUtc);

public sealed record PhysicalClassroom(string Id, string SchoolId, string Code, int Capacity);
public sealed record SectionRoomAssignment(string GradeId, string RoomId, string AcademicYear,
    string SchoolId, string Shift);

public sealed record SchoolInfrastructureRecord(string SchoolId, string GeographicArea,
    string PostalAddress, string BuildingType, string BuildingOwnership,
    int PhysicalRooms, string OperatingShifts, bool HasWater, bool HasElectricity,
    bool HasInternet, string Notes);

public sealed record SchoolFacility(string SchoolId, string Name, int Quantity, string Condition);

/// <summary>Employee identity stays constant across payroll school, secondment and workplace assignments.</summary>
public sealed record EmployeeAssignmentRecord(string Id, string EmployeeId,
    string PayrollSchoolId, string WorkplaceSchoolId, string AssignmentType,
    string? StartDate, string? EndDate, string Notes);

public sealed record EmployeeCareerRecord(string EmployeeId, string EmployeeCode, string FullName,
    string EmploymentCategory, string Qualification, string Specialization,
    int? QualificationYear, int? EmploymentYear, int? ContractYear,
    string OriginalPayrollSchoolId);
