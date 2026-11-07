using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The sales side of a harvest lot before sales invoices exist. "No sales" is the true answer while no invoice table does: every lot has zero kg sold and is on no invoice.
/// TEMPORARY: PR 6a (SALE-05, OMTS-224) replaces this class with a query on its invoice and return lines, and deletes it. Nothing else changes for the callers.
/// </summary>
internal sealed class NoSalesYetHarvestLotSalesQuery : IHarvestLotSalesQuery
{
    public Task<IReadOnlyDictionary<Guid, LotSalesFigures>> FiguresAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, LotSalesFigures>>(lotIds.Distinct().ToDictionary(id => id, _ => LotSalesFigures.None));

    public Task<bool> IsOnAnyInvoiceAsync(Guid lotId, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<IReadOnlySet<Guid>> LotsOnAnyInvoiceAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}
