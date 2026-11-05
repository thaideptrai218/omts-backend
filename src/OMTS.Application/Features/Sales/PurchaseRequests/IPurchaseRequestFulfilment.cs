namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>
/// The hook SALE-05 fills. A request is Fulfilled while a Completed invoice linked to it takes it in full (BR-SALE-01): worked
/// out, never stored, so a request whose invoice is cancelled (SALE-08) is Accepted again. Invoices do not exist yet, so
/// <see cref="NoInvoicesYetFulfilment"/> says none is. SALE-05 replaces it with the query on its invoices, and:
/// <list type="bullet">
/// <item>offers a request only when it is Accepted, not Fulfilled and its <c>AgreedPickupDate</c> is not more than 3 days ago;</item>
/// <item>calls <c>IUnitOfWork.MarkChanged</c> on the request when it saves a linked invoice, so that a trader's cancel at the
/// same moment conflicts (409 MSG06) and no invoice lands on a Cancelled request;</item>
/// <item>reads <c>PayLaterAgreed</c> for the Pay later term of each linked invoice, with no limit of kg (BR-SALE-07).</item>
/// </list>
/// </summary>
public interface IPurchaseRequestFulfilment
{
    /// <summary>The requests among <paramref name="requestIds"/> that are Fulfilled now.</summary>
    Task<IReadOnlySet<Guid>> FulfilledAmongAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken);
}

/// <summary>Until SALE-05: no invoice exists, so no request is Fulfilled.</summary>
internal sealed class NoInvoicesYetFulfilment : IPurchaseRequestFulfilment
{
    private static readonly IReadOnlySet<Guid> None = new HashSet<Guid>();

    public Task<IReadOnlySet<Guid>> FulfilledAmongAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken) => Task.FromResult(None);
}
