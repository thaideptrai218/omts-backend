using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The Mini App checkouts of the traders (<see cref="TraderCheckout"/>).</summary>
public interface ITraderCheckoutRepository
{
    void Add(TraderCheckout checkout);

    /// <summary>Tracked, with its payment (and the payment's bank account) and its invoice.</summary>
    Task<TraderCheckout?> GetForEditAsync(Guid checkoutId, CancellationToken cancellationToken);

    /// <summary>Read only, with its payment and its invoice.</summary>
    Task<TraderCheckout?> GetAsync(Guid checkoutId, CancellationToken cancellationToken);

    /// <summary>The checkout of this payOS order code, tracked, with its payment (and the payment's bank account) and its invoice.</summary>
    Task<TraderCheckout?> FindByOrderCodeAsync(long orderCode, CancellationToken cancellationToken);

    Task<bool> OrderCodeTakenAsync(long orderCode, CancellationToken cancellationToken);

    /// <summary>The newest checkout of the invoice whose payment still waits for its transfer, tracked, with its payment; null when there is none.</summary>
    Task<TraderCheckout?> FindPendingOfInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>
    /// The ids of the checkouts Zalo still has to be told about: a Zalo order, a Completed payment, no accepted report, fewer than <paramref name="maxAttempts"/> reports
    /// and none since <paramref name="lastAttemptBefore"/>; oldest first, at most <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListToReportAsync(DateTimeOffset lastAttemptBefore, int maxAttempts, int take, CancellationToken cancellationToken);
}
