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

    /// <summary>One page of the requests of a profile, newest first, with their products (SCR-SALE-01).</summary>
    Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>One page of the requests: those Awaiting response first, then newest first (SCR-SALE-02), with profiles and products.</summary>
    Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> SearchAsync(int? statusId, Guid? customerId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The Accepted requests of a profile whose agreed pickup date is not before <paramref name="earliestPickup"/>, with their products, the earliest pickup first (SALE-05.3).</summary>
    Task<IReadOnlyList<PurchaseRequest>> ListAcceptedOfCustomerAsync(Guid customerId, DateOnly earliestPickup, CancellationToken cancellationToken);

    /// <summary>The sendings kept for these requests, oldest first.</summary>
    Task<IReadOnlyList<PurchaseRequestSending>> ListSendingsAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken);
}
