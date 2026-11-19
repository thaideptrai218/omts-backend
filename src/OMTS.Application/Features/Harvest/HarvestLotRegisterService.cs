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
/// of a lot come from the sales side, so the sums, the order by remaining kg and the page are worked out over the lots the filters match; only the page reads the products, plans and names.
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

    public async Task<Result<HarvestLotRegisterPage>> SearchAsync(HarvestLotListQuery query, CancellationToken cancellationToken)
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

        var sort = query.Sort?.Trim().ToLowerInvariant() switch
        {
            null or "" or "code" => "code",
            "harvestedat" => "harvestedat",
            "remainingkg" => "remainingkg",
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
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var filter = new HarvestLotFilter(
            query.From, query.To, query.ZoneId, zones.AllZones ? null : zones.ZoneIds.ToHashSet(), query.RaftId, query.ProductId, query.StatusId, query.Weighed, closeReason, query.HasPlan, query.Search);
        var rows = await lots.SearchAsync(filter, cancellationToken);
        var figures = await sales.FiguresAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        var worked = rows
            .Select(r => (Row: r, Figures: HarvestLotFigures.Shown(r.StatusId, r.EstimatedKg, r.WeighedKg, r.LeftoverKg, figures[r.Id])))
            .ToList();
        var totals = new HarvestLotRegisterTotals(
            worked.Sum(w => w.Row.CageCount), worked.Sum(w => w.Row.EstimatedKg), worked.Sum(w => w.Row.WeighedKg ?? 0m), worked.Sum(w => w.Figures.SoldKg),
            worked.Sum(w => w.Figures.RemainingKg), worked.Sum(w => w.Figures.LostKg));

        // The lot code then breaks every tie, so a page never repeats or skips a lot.
        var ordered = sort switch
        {
            "harvestedat" => descending ? worked.OrderByDescending(w => w.Row.HarvestedAt) : worked.OrderBy(w => w.Row.HarvestedAt),
            "remainingkg" => descending ? worked.OrderByDescending(w => w.Figures.RemainingKg) : worked.OrderBy(w => w.Figures.RemainingKg),
            _ => descending ? worked.OrderByDescending(w => w.Row.LotCode, StringComparer.Ordinal) : worked.OrderBy(w => w.Row.LotCode, StringComparer.Ordinal),
        };
        var pageRows = (descending ? ordered.ThenByDescending(w => w.Row.LotCode, StringComparer.Ordinal) : ordered.ThenBy(w => w.Row.LotCode, StringComparer.Ordinal))
            .Skip((page - 1) * pageSize).Take(pageSize).ToList();

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
        return new HarvestLotRegisterPage(items, rows.Count, page, pageSize, totals);
    }

    /// <summary>The ledger rows of one lot, oldest first: the harvest at its weighed kg, the sales (with the order of their invoice) and disposals, the sales returns, what the close took off, and the reversals that undid any of them.</summary>
    public async Task<Result<IReadOnlyList<HarvestLotMovementView>>> MovementsAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
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
