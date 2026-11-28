using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkDayPlans;

public sealed record WorkDayPlanZoneRef(Guid Id, string Code, string Name);

public sealed record WorkDayPlanRaftRef(Guid Id, string Code);

public sealed record WorkDayPlanBatchRef(Guid Id, string Code);

/// <summary>The line of a harvest plan (KHT) a harvest plan was made with.</summary>
public sealed record WorkDayPlanHarvestRef(Guid HarvestPlanId, string HarvestPlanCode, Guid LineId, int LineNumber);

public sealed record WorkDayPlanAccountRef(Guid Id, string Name);

/// <summary>The numbers of a plan: <c>present</c> = <c>confirmed</c> + <c>outsiders</c>; <c>missing</c> is what is still needed (0 when full); <c>invited</c> are the invitations still waiting.</summary>
public sealed record WorkDayPlanStaffingView(int Required, int Confirmed, int Outsiders, int Present, int Missing, int Invited, bool Full);

/// <summary>What the caller may do with the plan now.</summary>
public sealed record WorkDayPlanAllowed(bool CanEdit, bool CanCancel, bool CanComplete, bool CanStaff, bool CanSetRequiredPeople, bool CanPayOutsiders);

/// <summary>One person of a plan. <c>hasAccount</c>: the employee could answer in an app; <c>assignmentId</c>: the work assignment a confirmed member has.</summary>
public sealed record WorkDayMemberView(
    Guid Id, Guid EmployeeId, string? Code, string Name, string? PhoneNumber, WorkDayMemberState State, DateTimeOffset InvitedAt, DateTimeOffset? Deadline,
    DateTimeOffset? RespondedAt, string? Reason, string? ConfirmedBy, bool HasAccount, Guid? AssignmentId, string RowVersion);

/// <summary>People hired outside the system for the day. <c>cashVoucherId</c> and <c>cashVoucherCode</c> are set once the line is paid.</summary>
public sealed record PlanOutsiderHireView(
    Guid Id, int People, decimal RatePerSession, decimal Amount, string? PayeeName, string? Note, bool Paid, Guid? CashVoucherId, string? CashVoucherCode,
    DateTimeOffset? PaidAt, string? PaidBy, string RowVersion);

/// <summary>A work-day plan. <c>members</c> and <c>outsiders</c> are filled by the detail routes and null in lists.</summary>
public sealed record WorkDayPlanItem(
    Guid Id,
    string Code,
    WorkDayPlanKind Kind,
    WorkDayPlanStatus Status,
    DateOnly WorkDate,
    DateTimeOffset? StartsAt,
    DateTimeOffset? ExpectedEndAt,
    WorkDayPlanZoneRef Zone,
    WorkDayPlanRaftRef? Raft,
    WorkDayPlanBatchRef? NurseryBatch,
    WorkDayPlanHarvestRef? HarvestPlan,
    WorkDayPlanAccountRef Responsible,
    string CreatedBy,
    int RequiredPeople,
    WorkDayPlanStaffingView Staffing,
    string? Note,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    WorkDayPlanAllowed Allowed,
    DateTimeOffset CreatedAt,
    string RowVersion,
    IReadOnlyList<WorkDayMemberView>? Members = null,
    IReadOnlyList<PlanOutsiderHireView>? Outsiders = null);

/// <summary>The plans of the days asked for. At most 62 days; <c>zoneIds</c> are the zones of the header filter (repeat the parameter, at most 200; empty = every zone the caller sees).</summary>
public sealed record WorkDayPlanListQuery(DateOnly From, DateOnly To, Guid[]? ZoneIds = null, WorkDayPlanKind? Kind = null, WorkDayPlanStatus? Status = null);

/// <summary>The plans of one day for the "invite into a plan" picker; <c>zoneIds</c> as in the list.</summary>
public sealed record WorkDayPlanDayQuery(DateOnly Date, Guid[]? ZoneIds = null);

/// <summary>
/// Create (POST) and update (PUT) of a Nursery or Seeding plan. A Seeding plan needs <c>raftId</c>; a Nursery plan needs <c>zoneId</c> (and may name the running <c>nurseryBatchId</c> of that zone for a care day).
/// <c>kind</c> and the place cannot change on update. A harvest plan is made with its line of a harvest plan; on update only <c>expectedEndAt</c>, <c>requiredPeople</c>, <c>note</c> and the person in charge change.
/// <c>clientOperationId</c> makes a repeated create one plan. <c>code</c> empty = the next KNL number on create, the stored one on update.
/// </summary>
public sealed record SaveWorkDayPlanRequest(
    WorkDayPlanKind? Kind,
    string? Code,
    DateOnly? WorkDate,
    DateTimeOffset? StartsAt,
    DateTimeOffset? ExpectedEndAt,
    Guid? ZoneId,
    Guid? RaftId,
    Guid? NurseryBatchId,
    Guid? ResponsibleAccountId,
    int? RequiredPeople,
    string? Note,
    Guid? ClientOperationId,
    string? RowVersion);

public sealed record CancelWorkDayPlanRequest(string? Reason, string? RowVersion);

public sealed record CompleteWorkDayPlanRequest(string? RowVersion);

/// <summary>Invite employees to the plan; <c>deadlineMinutes</c> is 15, 30 (default) or 60.</summary>
public sealed record InviteWorkDayPlanRequest(IReadOnlyList<Guid>? EmployeeIds, int? DeadlineMinutes);

/// <summary>Confirm for the person; the reason is required. <c>moveExisting</c> answers MSG-LABOR-06 (the person is moved from another raft or plan that day).</summary>
public sealed record ConfirmWorkDayMemberRequest(string? Reason, bool MoveExisting = false);

public sealed record RemoveWorkDayMemberRequest(string? Reason);

public sealed record SetWorkDayRequiredPeopleRequest(int? RequiredPeople, string? RowVersion);

public sealed record SaveOutsiderHireRequest(int? People, decimal? RatePerSession, string? PayeeName, string? Note, string? RowVersion = null);

/// <summary>Pay an outsider line: <c>method</c> Cash or BankTransfer (<c>cashAccountId</c> for a bank transfer), <c>paidAt</c> not in the future (default now), <c>paidById</c> an Owner or staff account (default the caller).</summary>
public sealed record PayOutsiderHireRequest(DateTimeOffset? PaidAt, PaymentMethod? Method, Guid? CashAccountId = null, Guid? PaidById = null, string? Note = null);

/// <summary>The people the person in charge can be for a place: the Owner and the Zone Managers of the zone.</summary>
public sealed record WorkDayPlanResponsible(Guid AccountId, string Name, bool IsCaller);

/// <summary>What a day looks like for the employees asked about, for the warnings of the picker (never a block).</summary>
public sealed record WorkDayAvailabilityRow(Guid EmployeeId, bool OnLeave, bool CheckedIn, Guid? PlanId, string? Place);

/// <summary>The plans of a harvest plan that still lack people (the answer of "Giao" with status 409).</summary>
public sealed record ShortWorkDayPlan(Guid PlanId, string Code, string? RaftCode, int Required, int Present);
