using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Publishing;

/// <summary>
/// TRACE-01: publishes the origin information of harvest lots, from the end of a sale (<see cref="IProvenancePublisher"/>) or from the Trace lot screen. Publishing saves the publish
/// time on the lot, which makes it Published (BR-TRACE-02: the state is worked out from the publish and withdrawal times, there are no versions), and writes one audit row (GBR-02).
/// Nothing is saved here: the caller's save holds the lots and their audit rows. Every write changes the lot row, so two publishes of one lot, or a publish and the end of a sale that
/// publishes it, cannot both save on the same version: the loser reads again.
/// </summary>
public sealed class ProvenancePublisher(
    IHarvestLotRepository lots, ProvenanceReadiness readiness, ZoneAccessGuard zoneAccess, AuditLogService audit, IUnitOfWork unitOfWork, TimeProvider timeProvider)
    : IProvenancePublisher
{
    public const string PublishedAction = "HarvestLot.Published";
    public const string RepublishedAction = "HarvestLot.Republished";

    public Task<IReadOnlyList<InvoiceLotOrigin>> CheckInvoiceLotsAsync(IReadOnlyCollection<Guid> harvestLotIds, CancellationToken cancellationToken) =>
        OriginsAsync(harvestLotIds, null, cancellationToken);

    public Task<IReadOnlyList<InvoiceLotOrigin>> PublishInvoiceLotsAsync(string salesInvoiceCode, IReadOnlyCollection<Guid> harvestLotIds, CancellationToken cancellationToken) =>
        OriginsAsync(harvestLotIds, salesInvoiceCode, cancellationToken);

    /// <summary>
    /// TRACE-01.0 step 4: the lot is Published from now; a Withdrawn lot is published again (TRACE-01.2) and no longer shows the withdrawal warning, while its withdrawal stays in the
    /// audit log. The lot comes tracked with its raft.
    /// </summary>
    /// <param name="salesInvoiceCode">The invoice at the end of whose sale the lot is published; null from the Trace lot screen.</param>
    public void Publish(HarvestLot lot, string? salesInvoiceCode)
    {
        var republished = lot.OriginState == HarvestLotOriginState.Withdrawn;
        var before = new { OriginState = lot.OriginState.ToString(), lot.PublishedAt, lot.WithdrawnAt };
        lot.PublishedAt = timeProvider.GetUtcNow();
        audit.Record(HarvestLotAudit.Entry(republished ? RepublishedAction : PublishedAction, lot, lot.Raft.ZoneId) with
        {
            Before = before,
            After = new { OriginState = lot.OriginState.ToString(), lot.PublishedAt, SalesInvoiceCode = salesInvoiceCode },
        });
    }

    // TRACE-01.0 steps 1-4 over the lots of one invoice: only lots that are Not published are checked and published. A lot of a zone where the caller may not publish (the invoice
    // zone is not always the lot's zone, BR-SALE-08) stays Not published and says why.
    private async Task<IReadOnlyList<InvoiceLotOrigin>> OriginsAsync(IReadOnlyCollection<Guid> harvestLotIds, string? publishFor, CancellationToken cancellationToken)
    {
        var found = await lots.ListForInvoiceAsync(harvestLotIds, cancellationToken);
        var notPublished = found.Where(l => l.OriginState == HarvestLotOriginState.NotPublished).ToList();
        var missing = await readiness.MissingAsync(notPublished.Select(l => l.Id).ToList(), cancellationToken);
        var zones = notPublished.Count == 0 ? PermittedZones.None : await zoneAccess.PermittedZonesAsync(PermissionFunctions.Provenance, PermissionAction.Publish, cancellationToken);
        var origins = new List<InvoiceLotOrigin>(found.Count);
        foreach (var lot in found)
        {
            if (lot.OriginState != HarvestLotOriginState.NotPublished)
            {
                if (publishFor is not null && lot.OriginState == HarvestLotOriginState.Published)
                {
                    // The QR code relies on it being Published: a withdrawal in the same moment (TRACE-04) makes one of the two saves fail, and the loser reads the lot again.
                    unitOfWork.MarkChanged(lot);
                }

                origins.Add(new InvoiceLotOrigin(lot.Id, lot.LotCode, lot.OriginState, []));
                continue;
            }

            var lacks = missing[lot.Id];
            if (lacks.Count == 0 && !zones.Allows(lot.Raft.ZoneId))
            {
                lacks = [OriginGap.NotPublisherOfZone(lot.Raft.Zone.Code)];
            }

            if (lacks.Count == 0 && publishFor is not null)
            {
                Publish(lot, publishFor);
            }

            origins.Add(new InvoiceLotOrigin(lot.Id, lot.LotCode, lot.OriginState, lacks));
        }

        return origins;
    }
}
