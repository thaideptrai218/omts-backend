using OMTS.Application.Features.Harvest;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// What HARVEST reads from SALE and CLAIM, which come later and depend on the lots: a port, so the two modules do not depend on each other in a circle. Until PR 6a a documented
/// "no sales yet" class answers it; 6a replaces it. TRACE adds what it reads from sales (forward trace, buyers to notify, lots of an invoice) with its own shapes in T1 and T3.
/// </summary>
public interface IHarvestLotSalesQuery
{
    /// <summary>The kg sold and returned of each lot (BR-HARVEST-07); every id asked for has an entry.</summary>
    Task<IReadOnlyDictionary<Guid, LotSalesFigures>> FiguresAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);

    /// <summary>The lot is on a sales invoice, also a cancelled one, which keeps its lines (BR-HARVEST-05, PM default).</summary>
    Task<bool> IsOnAnyInvoiceAsync(Guid lotId, CancellationToken cancellationToken);
}
