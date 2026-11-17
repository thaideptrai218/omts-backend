using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IWorkDayPlanRepository
{
    Task<WorkDayPlan?> GetAsync(Guid id, CancellationToken ct);
    Task<WorkDayPlan?> ForPurchaseAsync(Guid purchaseRequestId, CancellationToken ct);
    Task<WorkDayPlan?> FindOperationAsync(Guid creatorId, Guid operationId, CancellationToken ct);
    Task<IReadOnlyList<WorkDayPlan>> SearchAsync(DateOnly from, DateOnly to, Guid? responsibleId, CancellationToken ct);
    Task<IReadOnlyList<Account>> ResponsibleCandidatesAsync(CancellationToken ct);
    void Add(WorkDayPlan plan);
}
