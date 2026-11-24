using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The harvest lot list (all lots of the zones the caller can read, any status) and the product ledger of one lot, for a Zone Manager of the zone and the Cooperative Owner (Harvests View). The figures
/// of a lot come from the sales side: the count, the sums and the order by remaining kg are worked out by SQL over the lots the filters match, and only the page reads its figures, products, plans and names.
/// </summary>
public sealed class HarvestLotRegisterService(
    IHarvestLotRepository lots,
    IHarvestLotSalesQuery sales,
    IHarvestPlanRepository harvestPlans,
    IProductStockRepository stock,
    HarvestLotReferenceReader referenceReader,
    IAccountRepository accounts,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog)
{
    private const int MaxPageSize = 100;

    public Task<Result<HarvestLotRegisterPage>> SearchAsync(HarvestLotListQuery query, CancellationToken cancellationToken) =>
        ReadAsync(query, query.Page, query.PageSize, MaxPageSize, cancellationToken);

    /// <summary>The first <paramref name="maxRows"/> lots the filters match, in the list's order and with the same checks, read as one page: what the file of the list is made from.</summary>
    public Task<Result<HarvestLotRegisterPage>> FirstRowsAsync(HarvestLotListQuery query, int maxRows, CancellationToken cancellationToken) =>
        ReadAsync(query, 1, maxRows, maxRows, cancellationToken);

    private async Task<Result<HarvestLotRegisterPage>> ReadAsync(HarvestLotListQuery query, int requestedPage, int requestedSize, int maxPageSize, CancellationToken cancellationToken)
    {
        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.Harvests, PermissionAction.View, cancellationToken);
        if (zones.IsEmpty || (query.ZoneId is { } chosen && !zones.Allows(chosen)))
        {
            return AppError.Forbidden;
        }

        if (query.From > query.To)
        {
            return AppError.Invalid("Period");
        }

        HarvestLotCloseReason? closeReason = null;
        if (!string.IsNullOrWhiteSpace(query.CloseReason))
        {
            if (!Enum.TryParse<HarvestLotCloseReason>(query.CloseReason.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || query.CloseReason.Trim().All(char.IsDigit))
            {
                return AppError.Invalid("Close reason", "Use Unsold, Spoiled, TimeLimitReached or SoldWholeLot.");
            }

            closeReason = parsed;
        }

        HarvestLotSort? sort = query.Sort?.Trim().ToLowerInvariant() switch
        {
            null or "" or "code" => HarvestLotSort.Code,
            "harvestedat" => HarvestLotSort.HarvestedAt,
            "remainingkg" => HarvestLotSort.RemainingKg,
            _ => null,
        };
        if (sort is null)
        {
            return AppError.Invalid("Sort", "Use code, harvestedAt or remainingKg.");
        }

        if (query.SortDir?.Trim().ToLowerInvariant() is not (null or "" or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction", "Use asc or desc.");
        }

        var descending = !string.Equals(query.SortDir?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);
        var (page, pageSize) = (Math.Clamp(requestedPage, 1, int.MaxValue / maxPageSize), Math.Clamp(requestedSize, 1, maxPageSize));
        var filter = new HarvestLotFilter(
            query.From, query.To, query.ZoneId, zones.AllZones ? null : zones.ZoneIds.ToHashSet(), query.RaftId, query.ProductId, query.StatusId, query.Weighed, closeReason, query.HasPlan, query.Search);
        return await ReadPageAsync(filter, sort.Value, descending, page, pageSize, cancellationToken);
    }

    private async Task<HarvestLotRegisterPage> ReadPageAsync(HarvestLotFilter filter, HarvestLotSort sort, bool descending, int page, int pageSize, CancellationToken cancellationToken)
    {
        // The count and the sums come from one aggregate over every matching lot; only the page is read row by row, and only the page asks the sales side for its figures.
        var sums = await lots.SumsAsync(filter, cancellationToken);
        var rows = sums.Count == 0 ? [] : await lots.SearchPageAsync(filter, sort, descending, (page - 1) * pageSize, pageSize, cancellationToken);
        var figures = await sales.FiguresAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        var pageRows = rows
            .Select(r => (Row: r, Figures: HarvestLotFigures.Shown(r.StatusId, r.EstimatedKg, r.WeighedKg, r.LeftoverKg, figures[r.Id])))
            .ToList();
        var totals = new HarvestLotRegisterTotals(sums.CageCount, sums.EstimatedKg, sums.WeighedKg, sums.SoldKg, sums.RemainingKg, sums.LostKg);

        var ids = pageRows.Select(w => w.Row.Id).ToList();
        var productsOf = (await lots.ProductLinesOfAsync(ids, cancellationToken)).ToLookup(l => l.LotId);
        var planOf = (await harvestPlans.PlansOfLotsAsync(ids, cancellationToken)).ToDictionary(p => p.LotId);
        var people = pageRows.Select(w => w.Row.RecordedByAccountId).Distinct().ToList();
        var names = people.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(people, cancellationToken);
        var items = pageRows.Select(w => new HarvestLotRegisterItem(
            w.Row.Id, w.Row.LotCode, w.Row.HarvestedAt, w.Row.HarvestDate, w.Row.ZoneId, w.Row.ZoneCode, w.Row.RaftId, w.Row.RaftCode, w.Row.CycleId,
            productsOf[w.Row.Id].Select(p => new HarvestLotRegisterProduct(p.ProductId, p.Product.Code, p.Product.Name)).ToList(),
            w.Row.CageCount, w.Row.EstimatedKg, w.Row.WeighedKg, w.Row.WeighedAt, w.Figures, w.Row.StatusId, statusCatalog.DisplayNameOf(w.Row.StatusId), w.Row.CloseReason, w.Row.CloseDate,
            planOf.TryGetValue(w.Row.Id, out var plan) ? new HarvestLotPlanRef(plan.PlanId, plan.PlanCode) : null,
            names.GetValueOrDefault(w.Row.RecordedByAccountId))).ToList();
        return new HarvestLotRegisterPage(items, sums.Count, page, pageSize, totals);
    }

    /// <summary>The ledger rows of one lot, oldest first: the harvest at its weighed kg, the sales (with the order of their invoice) and disposals, the sales returns, what the close took off, and the reversals that undid any of them.</summary>
    public async Task<Result<IReadOnlyList<HarvestLotMovementView>>> MovementsAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetHeaderAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.View, lot.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var rows = await stock.ForLotAsync(lotId, cancellationToken);
        var orderOf = await referenceReader.OrderCodesOfSalesAsync(rows, cancellationToken);
        return Result<IReadOnlyList<HarvestLotMovementView>>.Success(rows.Select(m => new HarvestLotMovementView(
            m.Id, m.OccurredAt, m.Kind, m.DocumentId, m.DocumentCode, m.ProductId, m.Product.Code, m.Product.Name, m.QuantityKg, m.TransactionPrice, m.PartyName, m.PartyCode,
            m.Kind == ProductStockMovementKind.Sale && m.DocumentId is { } invoiceId ? orderOf.GetValueOrDefault(invoiceId) : null)).ToList());
    }
}
