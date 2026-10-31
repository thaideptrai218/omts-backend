using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IPurchaseRequestRepository
{
    Task<PurchaseRequest?> GetAsync(Guid id, CancellationToken ct);
    Task<PurchaseRequest?> FindOperationAsync(Guid customerId, Guid operationId, CancellationToken ct);
    Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> SearchAsync(Guid? customerId, int? statusId, int page, int pageSize, CancellationToken ct);
    void Add(PurchaseRequest request);
}
