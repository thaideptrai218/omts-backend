using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The lots for sale (Open or Reopened, BR-HARVEST-04): the list SALE-05 chooses from, in the zones the caller can access, and the count of the operations dashboard. A lot of a closed
/// cycle or a discontinued raft is still for sale, as BR-CYCLE-04 locks cultivation records, not sales. §I.4.4: the Owner and Zone Managers, not an Accountant.
/// </summary>
public sealed class HarvestLotForSaleService(IHarvestLotRepository lots, IHarvestLotSalesQuery sales, ZoneAccessGuard zoneAccess, IStatusCatalog statusCatalog)
{
    /// <param name="zoneId">One zone the caller can access, or null for all of them.</param>
    public async Task<Result<HarvestLotsForSale>> ListAsync(Guid? zoneId, CancellationToken cancellationToken)
    {
        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.Harvests, PermissionAction.View, cancellationToken);
        if (zones.IsEmpty || (zoneId is { } chosen && !zones.Allows(chosen)))
        {
            return AppError.Forbidden;
        }

        var forSale = await lots.ListForSaleAsync(ZoneIds(zones, zoneId), cancellationToken);
        var figures = await sales.FiguresAsync(forSale.Select(l => l.Id).ToList(), cancellationToken);
        var views = forSale.Select(l => new HarvestLotForSale(
            l.Id,
            l.LotCode,
            l.RaftId,
            l.Raft.Code,
            l.Raft.ZoneId,
            l.Raft.Zone.Code,
            l.Raft.Zone.Name,
            l.HarvestDate,
            HarvestLotFigures.Of(l, figures[l.Id]).RemainingKg,
            l.StatusId,
            statusCatalog.DisplayNameOf(l.StatusId),
            l.OriginState,
            l.OriginState == HarvestLotOriginState.Withdrawn ? l.WithdrawnAt : null)).ToList();
        var empty = views.Count == 0 ? UserMessages.NoDataForFilters(zoneId is null ? "zones" : "zone") : null;
        return new HarvestLotsForSale(views, empty?.Text, empty?.Code);
    }

    /// <summary>BR-FARM-10: the lots open for sale now, of one zone or of every zone. For the Owner alone (FARM-06): the caller has checked that.</summary>
    public async Task<HarvestLotForSaleCount> CountAsync(Guid? zoneId, CancellationToken cancellationToken) =>
        new(zoneId, await lots.CountForSaleAsync(zoneId, cancellationToken));

    private static IReadOnlyCollection<Guid>? ZoneIds(PermittedZones zones, Guid? zoneId) =>
        zoneId is { } chosen ? [chosen] : zones.AllZones ? null : zones.ZoneIds.ToList();
}
