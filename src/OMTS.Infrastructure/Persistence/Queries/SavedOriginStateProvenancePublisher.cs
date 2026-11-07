using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// Until TRACE-01 (publish traceability record) exists: publishes nothing and answers the origin state saved on each lot (BR-TRACE-02), so the QR code of an invoice is
/// created only for lots published before the end of the sale. TRACE-01 replaces this registration with the publisher that also publishes the ready lots.
/// </summary>
internal sealed class SavedOriginStateProvenancePublisher(OmtsDbContext db) : IProvenancePublisher
{
    public async Task<IReadOnlyList<InvoiceLotOrigin>> PublishInvoiceLotsAsync(IReadOnlyCollection<Guid> harvestLotIds, CancellationToken cancellationToken)
    {
        var lots = await db.HarvestLots.AsNoTracking().Where(l => harvestLotIds.Contains(l.Id)).ToListAsync(cancellationToken);
        return lots.Select(l => new InvoiceLotOrigin(l.Id, l.LotCode, l.OriginState, [])).ToList();
    }
}
