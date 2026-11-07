using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// The origin state of the lots of an invoice at the end of the sale, when the trader wants the QR code (SALE-05 steps 9-10, SALE-05.9). TRACE-01 extends this step:
/// its implementation will first publish, in the caller's save, the lots that are Not published and have all their origin information, and answer what each lot that
/// stays Not published misses (MSG-TRACE-01). Until TRACE-01 exists the implementation publishes nothing and answers the states saved on the lots (BR-TRACE-02).
/// </summary>
public interface IProvenancePublisher
{
    /// <summary>One entry per lot asked for, in no order.</summary>
    Task<IReadOnlyList<InvoiceLotOrigin>> PublishInvoiceLotsAsync(IReadOnlyCollection<Guid> harvestLotIds, CancellationToken cancellationToken);
}

/// <param name="Missing">What a lot that stays Not published misses for the provenance page; empty for the other states.</param>
public sealed record InvoiceLotOrigin(Guid HarvestLotId, string LotCode, HarvestLotOriginState State, IReadOnlyList<string> Missing);
