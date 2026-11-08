using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Publishing;

/// <summary>
/// TRACE-01 at the extension point "QR code requested" of the sale (SALE-05 steps 9-10, SALE-05.9): what SALE asks of the provenance of the lots of an invoice. A port, so SALE does not
/// depend on how TRACE checks and publishes.
/// </summary>
public interface IProvenancePublisher
{
    /// <summary>
    /// TRACE-01.0 steps 1-2, changing nothing: the state of each lot and, for a lot that is Not published, what it misses (BR-TRACE-01). A Not published lot with nothing missing is
    /// ready to publish. One entry per lot asked for, in no order.
    /// </summary>
    Task<IReadOnlyList<InvoiceLotOrigin>> CheckInvoiceLotsAsync(IReadOnlyCollection<Guid> harvestLotIds, CancellationToken cancellationToken);

    /// <summary>
    /// TRACE-01.0 step 4 and TRACE-01.1: publishes, in the caller's save, every lot that is Not published and passes the check, with one audit row each naming the invoice; the
    /// others keep their state (a lot is published once, a Withdrawn lot is published again only by the Owner on the Trace lot screen). One entry per lot asked for, as it is after the
    /// save, in no order.
    /// </summary>
    Task<IReadOnlyList<InvoiceLotOrigin>> PublishInvoiceLotsAsync(string salesInvoiceCode, IReadOnlyCollection<Guid> harvestLotIds, CancellationToken cancellationToken);
}

/// <param name="Missing">What a lot that is (or stays) Not published misses for the provenance page (MSG-TRACE-01); empty for the other states and for a lot that is ready.</param>
public sealed record InvoiceLotOrigin(Guid HarvestLotId, string LotCode, HarvestLotOriginState State, IReadOnlyList<OriginGap> Missing);
