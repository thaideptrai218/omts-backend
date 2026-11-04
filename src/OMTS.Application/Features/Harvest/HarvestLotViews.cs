using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>The lot answers, with signed photo links, the figures of the sales port and the commands the caller has. Every caller has already passed the zone check on the record.</summary>
public sealed class HarvestLotViews(
    IHarvestLotSalesQuery sales, IAccountRepository accounts, IFileStorage storage, IStatusCatalog statusCatalog, PermissionService permissions, ICurrentUser currentUser)
{
    /// <summary>BR-HARVEST-05: the Cooperative Owner corrects or removes any lot, a Zone Manager of the zone only a lot they recorded. Whether the cycle still allows it is asked apart.</summary>
    public static bool MayCorrect(AccountAccess? access, HarvestLot lot, Guid zoneId) =>
        access is not null
        && access.Allows(PermissionFunctions.Harvests, PermissionAction.Edit, PermissionScope.ZoneScoped, zoneId)
        && (access.IsCooperativeOwner || lot.RecordedByAccountId == access.AccountId);

    public async Task<AccountAccess?> AccessAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId ? await permissions.GetAccessAsync(accountId, cancellationToken) : null;

    /// <summary>One lot with its lanes, photos and cycle; the raft comes with its zone.</summary>
    public async Task<HarvestLotView> ViewAsync(HarvestLot lot, Raft raft, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync([lot.RecordedByAccountId], cancellationToken);
        var figures = await sales.FiguresAsync([lot.Id], cancellationToken);
        var canCorrect = CanEdit(lot.Cycle, raft) && MayCorrect(await AccessAsync(cancellationToken), lot, raft.ZoneId);
        var canRemove = canCorrect && !await sales.IsOnAnyInvoiceAsync(lot.Id, cancellationToken);
        var photos = new List<HarvestLotPhotoView>(lot.Photos.Count);
        foreach (var photo in lot.Photos.OrderBy(p => p.SortOrder))
        {
            photos.Add(new HarvestLotPhotoView(photo.FileKey, photo.SortOrder, await storage.GetReadUrlAsync(photo.FileKey, cancellationToken: cancellationToken)));
        }

        return new HarvestLotView(
            lot.Id,
            lot.LotCode,
            lot.CycleId,
            raft.Id,
            raft.Code,
            raft.ZoneId,
            raft.Zone.Code,
            lot.HarvestedAt,
            lot.HarvestDate,
            LaneNumbers(lot),
            FinishedLaneNumbers(lot),
            lot.CageCount,
            lot.KgPerCage,
            lot.EstimatedKg,
            HarvestLotFigures.Of(lot, figures[lot.Id]),
            lot.StatusId,
            statusCatalog.DisplayNameOf(lot.StatusId),
            lot.ReopenedOn,
            lot.CloseDate,
            lot.LeftoverKg,
            lot.CloseReason,
            lot.OriginState,
            lot.PublishedAt,
            lot.WithdrawnAt,
            photos,
            lot.RecordedByAccountId,
            names.GetValueOrDefault(lot.RecordedByAccountId),
            canCorrect,
            canRemove,
            RowVersionGuard.Encode(lot.RowVersion));
    }

    /// <summary>The lots of one cycle with their lanes: lots for sale first, then the latest harvest first (SCR-HARVEST-01). The raft comes with its zone.</summary>
    public async Task<IReadOnlyList<HarvestLotListItem>> ListAsync(IReadOnlyList<HarvestLot> lots, CultivationCycle cycle, Raft raft, CancellationToken cancellationToken)
    {
        var ids = lots.Select(l => l.Id).ToList();
        var figures = await sales.FiguresAsync(ids, cancellationToken);
        var onInvoice = await sales.LotsOnAnyInvoiceAsync(ids, cancellationToken);
        var editable = CanEdit(cycle, raft);
        var access = await AccessAsync(cancellationToken);
        return lots
            .OrderByDescending(l => l.IsForSale).ThenByDescending(l => l.HarvestedAt)
            .Select(lot =>
            {
                var worked = HarvestLotFigures.Of(lot, figures[lot.Id]);
                var canCorrect = editable && MayCorrect(access, lot, raft.ZoneId);
                return new HarvestLotListItem(
                    lot.Id, lot.LotCode, lot.HarvestedAt, lot.HarvestDate, LaneNumbers(lot), FinishedLaneNumbers(lot), lot.CageCount, lot.KgPerCage, lot.EstimatedKg,
                    worked.InvoicedKg, worked.RemainingKg, lot.StatusId, statusCatalog.DisplayNameOf(lot.StatusId), lot.OriginState,
                    canCorrect, canCorrect && !onInvoice.Contains(lot.Id), RowVersionGuard.Encode(lot.RowVersion));
            })
            .ToList();
    }

    /// <summary>BR-HARVEST-05, as the writes answer it: an Open cycle, with neither the raft nor the zone Discontinued.</summary>
    private static bool CanEdit(CultivationCycle cycle, Raft raft) => CultivationCycleViews.CanEdit(cycle, raft, raft.Zone.StatusId == StatusIds.Usage.InUse);

    private static List<int> LaneNumbers(HarvestLot lot) => lot.Lanes.Select(l => l.Lane.LaneNumber).Order().ToList();

    private static List<int> FinishedLaneNumbers(HarvestLot lot) => lot.Lanes.Where(l => l.IsFinished).Select(l => l.Lane.LaneNumber).Order().ToList();
}
