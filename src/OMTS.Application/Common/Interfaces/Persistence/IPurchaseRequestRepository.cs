using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>One SMS or Zalo OA sending of the response to a purchase request (BR-SALE-09).</summary>
public sealed record PurchaseRequestSending(Guid RequestId, NotificationChannel Channel, string? PhoneNumber, int? DeliveryStatusId, DateTimeOffset? DeliveryStatusAt);

public interface IPurchaseRequestRepository
{
    void Add(PurchaseRequest request);

    /// <summary>The request with its customer profile and product, tracked.</summary>
    Task<PurchaseRequest?> GetForEditAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>The request with its customer profile, product and shift invitations with their employees, tracked: what a status change of it touches.</summary>
    Task<PurchaseRequest?> GetAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>One page of the requests of a profile, newest first, with their products (SCR-SALE-01).</summary>
    Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the requests: those still to be answered first (Awaiting response or Awaiting staff), then newest first (SCR-SALE-02),
    /// with profiles, products and shift invitations.
    /// </summary>
    Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> SearchAsync(int? statusId, Guid? customerId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The sendings kept for these requests, oldest first.</summary>
    Task<IReadOnlyList<PurchaseRequestSending>> ListSendingsAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken);

    /// <summary>The request a profile sent with this client operation, if any: a retry of the same send.</summary>
    Task<PurchaseRequest?> FindOperationAsync(Guid customerId, Guid operationId, CancellationToken cancellationToken);

    /// <param name="zoneIds">Only the employees with a working zone among these zones; null: every employee.</param>
    Task<IReadOnlyList<Employee>> StaffingEmployeesAsync(IReadOnlyCollection<Guid>? zoneIds, CancellationToken ct);
    Task<IReadOnlyList<PurchaseShiftInvitation>> EmployeeInvitationsAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<PurchaseShiftInvitation?> InvitationAsync(Guid id, CancellationToken ct);
    Task<bool> HasShiftReservationAsync(Guid employeeId, DateOnly day, Guid? exceptRequestId, CancellationToken ct);
    void AddInvitation(PurchaseShiftInvitation invitation);
}
