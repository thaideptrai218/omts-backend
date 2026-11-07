using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>The lot answers, with signed photo links and the figures of the sales port. Every caller has already passed the zone check on the record.</summary>
public sealed class HarvestLotViews(IHarvestLotSalesQuery sales, IAccountRepository accounts, IFileStorage storage, IStatusCatalog statusCatalog)
{
    /// <summary>One lot with its lanes and photos; the raft comes with its zone.</summary>
    public async Task<HarvestLotView> ViewAsync(HarvestLot lot, Raft raft, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync([lot.RecordedByAccountId], cancellationToken);
        var figures = await sales.FiguresAsync([lot.Id], cancellationToken);
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
            RowVersionGuard.Encode(lot.RowVersion));
    }

    /// <summary>The lots with their lanes: lots for sale first, then the latest harvest first (SCR-HARVEST-01).</summary>
    public async Task<IReadOnlyList<HarvestLotListItem>> ListAsync(IReadOnlyList<HarvestLot> lots, CancellationToken cancellationToken)
    {
        var figures = await sales.FiguresAsync(lots.Select(l => l.Id).ToList(), cancellationToken);
        return lots
            .OrderByDescending(l => l.IsForSale).ThenByDescending(l => l.HarvestedAt)
            .Select(lot =>
            {
                var worked = HarvestLotFigures.Of(lot, figures[lot.Id]);
                return new HarvestLotListItem(
                    lot.Id, lot.LotCode, lot.HarvestedAt, lot.HarvestDate, LaneNumbers(lot), FinishedLaneNumbers(lot), lot.CageCount, lot.KgPerCage, lot.EstimatedKg,
                    worked.InvoicedKg, worked.RemainingKg, lot.StatusId, statusCatalog.DisplayNameOf(lot.StatusId), lot.OriginState, RowVersionGuard.Encode(lot.RowVersion));
            })
            .ToList();
    }

    private static List<int> LaneNumbers(HarvestLot lot) => lot.Lanes.Select(l => l.Lane.LaneNumber).Order().ToList();

    private static List<int> FinishedLaneNumbers(HarvestLot lot) => lot.Lanes.Where(l => l.IsFinished).Select(l => l.Lane.LaneNumber).Order().ToList();
}
