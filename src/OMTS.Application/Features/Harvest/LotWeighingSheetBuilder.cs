using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The weighing sheet of a lot as the cooperative and the trader of the lot both see it (owner decision of 24/11/2026): the cooperative's cages, the official weighing, beside the trader's own cages,
/// compared by position, and the trader's agreement to the completed weighing. The lot must be read with its product lines and the cages of both sides.
/// </summary>
public sealed class LotWeighingSheetBuilder(IHarvestLotRepository lots, IStatusCatalog statusCatalog)
{
    /// <param name="canEdit">The caller may weigh its own side's cages now (the lot not Closed is checked here).</param>
    /// <param name="traderView">The sheet is for the trader of the lot, who may agree to the weighing.</param>
    public async Task<LotWeighingSheet> BuildAsync(HarvestLot lot, bool canEdit, bool traderView, CancellationToken cancellationToken) =>
        Build(lot, (await lots.OrdersOfAsync([lot.Id], cancellationToken))[lot.Id], canEdit, traderView);

    public LotWeighingSheet Build(HarvestLot lot, HarvestLotOrders orders, bool canEdit, bool traderView)
    {
        var cages = lot.Cages.OrderBy(c => c.Number).ToList();
        var traderCages = orders.Trader is null ? [] : lot.TraderCages.OrderBy(c => c.Number).ToList();
        var products = lot.ProductLines.OrderBy(l => l.LineNumber).Select(line =>
        {
            var mine = cages.Where(c => c.ProductId == line.ProductId).ToList();
            return new LotCageProductTotal(
                line.LineNumber, line.ProductId, line.Product?.Code ?? string.Empty, line.Product?.Name ?? string.Empty, line.CageCount, mine.Count, mine.Sum(c => c.Kg), line.WeighedKg);
        }).ToList();

        var open = lot.StatusId != StatusIds.HarvestLot.Closed;
        var needsCompleting = NeedsCompleting(lot, cages);
        var agreed = orders.Trader is not null && lot.IsWeighingAgreed;
        return new LotWeighingSheet(
            lot.Id, lot.LotCode, lot.StatusId, statusCatalog.DisplayNameOf(lot.StatusId), RowVersionGuard.Encode(lot.RowVersion), lot.CageCount, lot.EstimatedKg,
            [.. cages.Select(c => new LotCageView(c.Id, c.Number, c.ProductId, c.Kg, c.WeighedAt))],
            products, cages.Sum(c => c.Kg), lot.WeighedKg, lot.WeighedAt, needsCompleting, canEdit && open,
            orders.OrderCodes, orders.Trader?.Name,
            [.. traderCages.Select(c => new LotTraderCageView(c.Id, c.Number, c.Kg, c.WeighedAt))], traderCages.Sum(c => c.Kg),
            agreed ? new LotWeighingAgreement(lot.WeighingAgreedAt!.Value, lot.WeighingAgreedKg!.Value) : null,
            orders.Trader is not null && !agreed,
            traderView && open && lot.WeighedKg is not null && !needsCompleting && !agreed);
    }

    /// <summary>The cages differ from the last completed weighing; a lot with no cage (weighed before cages, or not started) has nothing to complete.</summary>
    internal static bool NeedsCompleting(HarvestLot lot, IReadOnlyList<HarvestLotCage> cages)
    {
        if (cages.Count == 0)
        {
            return false;
        }

        if (lot.WeighedKg is null)
        {
            return true;
        }

        if (lot.ProductLines.Count == 0)
        {
            return cages.Sum(c => c.Kg) != lot.WeighedKg;
        }

        var products = lot.ProductLines.Select(l => l.ProductId).ToHashSet();
        return cages.Any(c => c.ProductId is not { } id || !products.Contains(id))
            || lot.ProductLines.Any(l => cages.Where(c => c.ProductId == l.ProductId).Sum(c => c.Kg) != l.WeighedKg);
    }
}
