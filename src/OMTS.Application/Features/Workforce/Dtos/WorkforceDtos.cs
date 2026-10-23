namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record TeamRef(Guid Id, string Name);

public sealed record WorkerRef(Guid Id, string FullName);

public sealed record LinkedEmployeeRef(Guid Id, string FullName);

/// <param name="TeamId">Current team; an In Use team.</param>
/// <param name="EmployeeId">The same person is also an employee; set automatically on create when exactly one Employed employee has the same name and phone.</param>
public sealed record SaveWorkerRequest(
    string? FullName,
    string? PhoneNumber,
    string? CitizenId,
    Guid? TeamId,
    Guid? EmployeeId,
    string? RowVersion = null);

/// <summary>Worker with personal data: only for LABOR_WORKERS viewers, the Accountant and the Owner.</summary>
public sealed record WorkerView(
    Guid Id,
    string FullName,
    string? PhoneNumber,
    string? CitizenId,
    TeamRef? Team,
    LinkedEmployeeRef? Employee,
    bool IsTeamLeader,
    int StatusId,
    string Status,
    string RowVersion);

public sealed record WorkerListItem(Guid Id, string FullName, string? PhoneNumber, string? CitizenId, TeamRef? Team, int StatusId, string Status);

/// <param name="LeaderWorkerId">Crew leader: must be a current member of the team.</param>
public sealed record SaveTeamRequest(
    string? Name,
    Guid? LeaderWorkerId,
    string? PhoneNumber,
    string? OtherContacts,
    string? RowVersion = null);

public sealed record TeamMember(Guid Id, string FullName, string? PhoneNumber, string Status);

/// <param name="PhoneNumber">As entered (may be empty).</param>
/// <param name="ContactPhoneNumber">The phone entered, else the phone of the leader.</param>
public sealed record TeamView(
    Guid Id,
    string Name,
    WorkerRef? Leader,
    string? PhoneNumber,
    string? ContactPhoneNumber,
    string? OtherContacts,
    int StatusId,
    string Status,
    IReadOnlyList<TeamMember> Members,
    string RowVersion);

public sealed record TeamListItem(Guid Id, string Name, WorkerRef? Leader, string? ContactPhoneNumber, int MemberCount, int StatusId, string Status);

/// <summary>Picker row of LAB-03/LAB-04: no personal data.</summary>
/// <param name="Kind">Employee, Worker or Team.</param>
public sealed record LaborPickerItem(string Kind, Guid Id, string Name, TeamRef? Team, string Status);
