using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// How the staffing of a plan stands against the people the Owner needs (owner decision of 26/11/2026). <paramref name="Confirmed"/> counts the employees who accepted (in the mobile app or confirmed by the Owner for them),
/// <paramref name="External"/> the people added outside the system; both are present. <paramref name="Invited"/> still wait for an answer, <paramref name="Declined"/> said no. <paramref name="Ready"/> is true when nobody is missing
/// (confirmed + external at least <paramref name="RequiredPeople"/>, or no number was set): it never blocks anything, the screen only warns when it is false.
/// </summary>
public sealed record HarvestPlanStaffingSummary(int? RequiredPeople, int Confirmed, int Invited, int Declined, int External, int Missing, bool Ready);

/// <summary>
/// One person of the roster. <paramref name="Type"/> is the type in force today (Staff, SeasonalWorker); <paramref name="HasAccount"/> says whether the employee can answer in the app (without one only the Owner can confirm).
/// <paramref name="Reason"/> is why the Owner confirmed (ConfirmedManually) or the optional reason of a decline; <paramref name="ConfirmedBy"/> the person who confirmed manually.
/// </summary>
public sealed record HarvestPlanRosterRow(
    Guid Id,
    Guid EmployeeId,
    string? Code,
    string Name,
    string? PhoneNumber,
    EmployeeType? Type,
    bool HasAccount,
    HarvestPlanStaffState State,
    DateTimeOffset InvitedAt,
    DateTimeOffset? RespondedAt,
    string? Reason,
    string? ConfirmedBy,
    string RowVersion);

/// <summary>
/// The roster of a plan with its numbers, for a screen that polls it. <paramref name="RosterVersion"/> changes whenever the roster or the number needed changes (it is also the ETag of the roster route);
/// <paramref name="RowVersion"/> is the plan's. <paramref name="CanEdit"/>: the caller may change the roster now.
/// </summary>
public sealed record HarvestPlanRoster(
    Guid PlanId, string PlanCode, string RowVersion, string RosterVersion, int? RequiredPeople, HarvestPlanStaffingSummary Summary, IReadOnlyList<HarvestPlanRosterRow> People, bool CanEdit);

/// <summary>Choose people for the plan (POST invite): the employees not on the roster yet are added as Invited and told; the others are skipped.</summary>
public sealed record InviteHarvestStaffRequest(IReadOnlyList<Guid>? EmployeeIds);

/// <summary>
/// The whole roster as the Owner picked it (PUT roster): employees listed and not on the roster are invited; Invited, Declined and Confirmed rows of employees not listed are taken off (people added outside the system stay).
/// <paramref name="RequiredPeople"/> is the number needed, null clears it; <paramref name="RowVersion"/> is the plan's, checked when sent.
/// </summary>
public sealed record SaveHarvestRosterRequest(IReadOnlyList<Guid>? EmployeeIds, int? RequiredPeople, string? RowVersion = null);

/// <summary>The Owner (or the Zone Manager of the plan) confirms on behalf of an employee: the reason is required (at most 500 characters).</summary>
public sealed record ConfirmHarvestStaffRequest(string? Reason);

/// <summary>Takes a person off the roster; the reason is optional (at most 500 characters).</summary>
public sealed record RemoveHarvestStaffRequest(string? Reason = null);

/// <summary>
/// "Bổ sung nhân công": a person hired on the spot outside the system. The name and the phone number are required; a seasonal worker record is made for them, with its login account (same rules as saving a seasonal worker).
/// <paramref name="OwnerApproved"/> is only for a Zone Manager, who confirms the Owner approved in person (as when adding an employee).
/// </summary>
public sealed record AddExternalHarvestStaffRequest(string? FullName, string? PhoneNumber, string? Note = null, string? Address = null, bool OwnerApproved = false);

/// <summary>The answer of "Bổ sung nhân công": the roster and the new row. The person gets no login account.</summary>
public sealed record HarvestPlanExternalAdded(HarvestPlanRoster Roster, HarvestPlanRosterRow Person);

/// <summary>The number of people the plan needs (PUT required): 1 to 500, null clears it; <paramref name="RowVersion"/> is the plan's, checked when sent.</summary>
public sealed record SetRequiredPeopleRequest(int? RequiredPeople, string? RowVersion = null);

/// <summary>An employee answers an invitation (POST /api/me/harvest-shifts/{rowId}/response): <paramref name="Accept"/> is required; the reason of a decline is optional (at most 500 characters).</summary>
public sealed record RespondHarvestShiftRequest(bool? Accept, string? Reason = null);

/// <summary>A raft of a plan an employee is invited to, without the customer or the cages.</summary>
public sealed record MyHarvestShiftRaft(string RaftCode, string ZoneCode);

/// <summary>An invitation as the employee sees it: the plan, when and where, its own answer and how many have confirmed.</summary>
public sealed record MyHarvestShift(
    Guid RowId,
    Guid PlanId,
    string PlanCode,
    int PlanStatusId,
    string PlanStatus,
    DateTimeOffset PlannedAt,
    HarvestPlanExecutor Executor,
    IReadOnlyList<MyHarvestShiftRaft> Rafts,
    HarvestPlanStaffState State,
    DateTimeOffset InvitedAt,
    DateTimeOffset? RespondedAt,
    string? Reason,
    bool CanRespond,
    HarvestPlanStaffingSummary Staffing,
    string RowVersion);
