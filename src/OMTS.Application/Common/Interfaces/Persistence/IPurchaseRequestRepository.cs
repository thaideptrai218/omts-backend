using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IPurchaseRequestRepository
{
    void Add(PurchaseRequest request);

    /// <summary>The request with its customer profile and product, tracked.</summary>
    Task<PurchaseRequest?> GetForEditAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>The Accepted requests of a profile whose agreed pickup date is not before <paramref name="earliestPickup"/>, with their products, the earliest pickup first (SALE-05.3).</summary>
    Task<IReadOnlyList<PurchaseRequest>> ListAcceptedOfCustomerAsync(Guid customerId, DateOnly earliestPickup, CancellationToken cancellationToken);

    Task<PurchaseRequest?> GetAsync(Guid id, CancellationToken ct);
    Task<PurchaseRequest?> FindOperationAsync(Guid customerId, Guid operationId, CancellationToken ct);
    Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> SearchAsync(Guid? customerId, int? statusId, int page, int pageSize, CancellationToken ct);
    Task<IReadOnlyList<Employee>> StaffingEmployeesAsync(CancellationToken ct);
    Task<IReadOnlyList<PurchaseShiftInvitation>> EmployeeInvitationsAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<PurchaseShiftInvitation?> InvitationAsync(Guid id, CancellationToken ct);
    Task<bool> HasShiftReservationAsync(Guid employeeId, DateOnly day, Guid? exceptRequestId, CancellationToken ct);
    void AddInvitation(PurchaseShiftInvitation invitation);
}
