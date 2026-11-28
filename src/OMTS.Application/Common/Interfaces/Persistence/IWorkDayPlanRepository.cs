using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The plans a list reads: the days, the zones (null = every zone), and optionally one kind and one status.</summary>
public sealed record WorkDayPlanFilter(DateOnly From, DateOnly To, IReadOnlyCollection<Guid>? ZoneIds, WorkDayPlanKind? Kind, WorkDayPlanStatus? Status);

public interface IWorkDayPlanRepository
{
    // ---- The older plan of one raft and one job type (api/work-plans) ----

    Task<WorkDayPlan?> GetAsync(Guid id, CancellationToken ct);
    Task<WorkDayPlan?> ForPurchaseAsync(Guid purchaseRequestId, CancellationToken ct);
    Task<WorkDayPlan?> FindOperationAsync(Guid creatorId, Guid operationId, CancellationToken ct);
    Task<IReadOnlyList<WorkDayPlan>> SearchAsync(DateOnly from, DateOnly to, Guid? responsibleId, CancellationToken ct);
    Task<IReadOnlyList<Account>> ResponsibleCandidatesAsync(CancellationToken ct);
    void Add(WorkDayPlan plan);

    // ---- The work-day plans (api/work-day-plans) ----

    /// <summary>The plan with its zone, raft, nursery batch, people in charge, harvest line (and its harvest plan), members (with employee and account) and outsider lines, tracked.</summary>
    Task<WorkDayPlan?> GetForEditAsync(Guid id, CancellationToken ct);

    /// <summary>The plan of a harvest plan line, as <see cref="GetForEditAsync"/>, tracked.</summary>
    Task<WorkDayPlan?> GetByLineForEditAsync(Guid lineId, CancellationToken ct);

    /// <summary>The plans of these lines, as <see cref="GetForEditAsync"/>, tracked.</summary>
    Task<IReadOnlyList<WorkDayPlan>> ListByLinesForEditAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken ct);

    /// <summary>The plan of the creator with this client operation id, tracked.</summary>
    Task<WorkDayPlan?> FindOperationForEditAsync(Guid creatorId, Guid operationId, CancellationToken ct);

    /// <summary>Another plan with the code regardless of letter case; cancelled ones included. Untracked.</summary>
    Task<WorkDayPlan?> FindByCodeAsync(string code, Guid? exceptPlanId, CancellationToken ct);

    /// <summary>The plans of the days (and zones, kind and status) with their members and outsider lines, untracked, by date then code.</summary>
    Task<IReadOnlyList<WorkDayPlan>> ListAsync(WorkDayPlanFilter filter, CancellationToken ct);

    /// <summary>The ids of the plans that have an invitation past its deadline.</summary>
    Task<IReadOnlyList<Guid>> ListPlanIdsWithOverdueInvitationsAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>The members of these employees on the plans of the day other than <paramref name="exceptPlanId"/>, with their plan, tracked.</summary>
    Task<IReadOnlyList<WorkDayPlanMember>> ListMembersOnDayAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, Guid exceptPlanId, CancellationToken ct);

    /// <summary>The outsider lines of the plans of the days that are not cancelled, with their plan, untracked.</summary>
    Task<IReadOnlyList<PlanOutsiderHire>> ListOutsiderHiresAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>The outsider line with its plan (and the plan's people), tracked.</summary>
    Task<PlanOutsiderHire?> GetOutsiderForEditAsync(Guid id, CancellationToken ct);

    /// <summary>The code of a cash voucher by id, for the answer of an outsider line.</summary>
    Task<IReadOnlyDictionary<Guid, string>> VoucherCodesAsync(IReadOnlyCollection<Guid> voucherIds, CancellationToken ct);

    void AddMember(WorkDayPlanMember member);
    void RemoveMember(WorkDayPlanMember member);
    void AddOutsider(PlanOutsiderHire hire);
    void RemoveOutsider(PlanOutsiderHire hire);
}
