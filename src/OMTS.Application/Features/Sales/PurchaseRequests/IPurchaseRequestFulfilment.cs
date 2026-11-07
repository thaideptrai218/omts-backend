namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>
/// A request is Fulfilled while a Completed invoice linked to it takes it in full (BR-SALE-01): worked out from the invoices, never stored, so a request whose invoice is cancelled
/// (SALE-08) is Accepted again. SALE-05 offers a request only when it is Accepted, not Fulfilled and its <c>AgreedPickupDate</c> is not more than 3 days ago, marks the request changed
/// when it saves a linked invoice (so a trader's cancel at the same moment conflicts and no invoice lands on a Cancelled request), and reads <c>PayLaterAgreed</c> for each linked invoice.
/// </summary>
public interface IPurchaseRequestFulfilment
{
    /// <summary>The requests among <paramref name="requestIds"/> that are Fulfilled now.</summary>
    Task<IReadOnlySet<Guid>> FulfilledAmongAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken);
}
