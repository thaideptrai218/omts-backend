using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>NURSERY-01/02 reads (ươm giống), for a Zone Manager of the zone and the Cooperative Owner: the seed lots to choose from, the list, one batch and the batches with seed left to hang.</summary>
public sealed class NurseryReadService(
    INurseryBatchRepository nurseries,
    ISeedLotRepository lots,
    IFarmingZoneRepository zones,
    NurseryBatchViews views,
    ZoneAccessGuard zoneAccess)
{
    private const int MaxPageSize = 100;

    /// <summary>The seed lots a nursery in the zone can take: a Seed line of a Completed receipt with something on hand, those received last first. With none: MSG-CYCLE-03.</summary>
    public async Task<Result<NurserySeedLotList>> SeedLotsAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        if ((await zones.GetByIdsAsync([zoneId], cancellationToken)).Count == 0)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Create, zoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var options = (await lots.ListOfferedAsync(Guid.Empty, cancellationToken))
            .Select(l => new SeedLotOption(
                l.LineId, DocumentCodes.SeedLotCode(l.ReceiptCode, l.LineNumber), l.ReceiptId, l.ReceiptCode, l.ReceivedDate, l.ItemId, l.ItemCode, l.ItemName, l.Unit, l.SupplierId, l.SupplierName,
                l.QuantityReceived, l.QuantitySeeded, false, l.QuantityReturned, l.QuantityConsumed))
            .Where(l => l.OnHand > 0)
            .ToList();
        var empty = options.Count == 0 ? UserMessages.NoSeedLot : null;
        return new NurserySeedLotList(zoneId, options, empty?.Text, empty?.Code);
    }

    public async Task<Result<NurseryBatchPage>> SearchAsync(NurseryListQuery query, CancellationToken cancellationToken)
    {
        var zonesAllowed = await zoneAccess.PermittedZonesAsync(PermissionFunctions.CultivationCycles, PermissionAction.View, cancellationToken);
        if (zonesAllowed.IsEmpty || (query.ZoneId is { } chosen && !zonesAllowed.Allows(chosen)))
        {
            return AppError.Forbidden;
        }

        var statuses = StatusesOf(query.Status);
        if (statuses.Error is not null)
        {
            return statuses.Error;
        }

        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var filter = new NurseryBatchFilter(
            query.ZoneId, zonesAllowed.AllZones ? null : zonesAllowed.ZoneIds.ToHashSet(), statuses.Ids, query.From, query.To, query.Search, SortOf(query.SortBy),
            !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase));
        var (rows, total, totals) = await nurseries.SearchAsync(filter, page, pageSize, cancellationToken);
        var seeded = await nurseries.SeededByBatchAsync(rows.Select(r => r.Id).ToList(), null, cancellationToken);
        return new NurseryBatchPage(rows.Select(r => views.ListItem(r, seeded)).ToList(), page, pageSize, total, NurseryBatchViews.Summary(totals));
    }

    public async Task<Result<NurseryBatchView>> GetAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await nurseries.GetAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return AppError.NotFound;
        }

        return await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.View, batch.ZoneId, cancellationToken)
            ? await views.ViewAsync(batch, cancellationToken)
            : AppError.Forbidden;
    }

    /// <summary>The batches ready to start a cycle from (v2): Completed ("đã ươm xong") with pieces left in the pool, the one completed last listed first.</summary>
    public async Task<Result<AvailableNurseryList>> AvailableAsync(Guid? zoneId, CancellationToken cancellationToken)
    {
        var allowed = await zoneAccess.PermittedZonesAsync(PermissionFunctions.CultivationCycles, PermissionAction.View, cancellationToken);
        if (allowed.IsEmpty || (zoneId is { } chosen && !allowed.Allows(chosen)))
        {
            return AppError.Forbidden;
        }

        var completed = await nurseries.CompletedAsync(zoneId, allowed.AllZones ? null : allowed.ZoneIds.ToHashSet(), cancellationToken);
        var seeded = await nurseries.SeededByBatchAsync(completed.Select(b => b.Id).ToList(), null, cancellationToken);
        var batches = completed
            .Select(b => (Batch: b, Seeded: seeded.GetValueOrDefault(b.Id)))
            .Select(x => (x.Batch, x.Seeded, Remaining: NurseryRules.Remaining(x.Batch.Survivors ?? 0m, x.Seeded)))
            .Where(x => x.Remaining > 0)
            .OrderByDescending(x => x.Batch.CompletedOn).ThenByDescending(x => x.Batch.Code, StringComparer.Ordinal)
            .Select(x => new AvailableNurseryBatch(
                x.Batch.Id, x.Batch.Code, x.Batch.ZoneId, x.Batch.Zone.Code, SeedingValues.LotCode(x.Batch.SeedLotLine), x.Batch.SeedLotLine.Item?.Name, NurseryBatchViews.UnitOf(x.Batch.SeedLotLine),
                x.Batch.QuantityIn, x.Batch.Survivors ?? 0m, x.Seeded, x.Remaining, x.Batch.CompletedOn))
            .ToList();
        return new AvailableNurseryList(batches);
    }

    private static (IReadOnlyList<int>? Ids, AppError? Error) StatusesOf(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => (null, null),
        "nursing" => ([StatusIds.NurseryBatch.Nursing], null),
        "completed" => ([StatusIds.NurseryBatch.Completed], null),
        "cancelled" => ([StatusIds.NurseryBatch.Cancelled], null),
        "closed" => ([StatusIds.NurseryBatch.Closed], null),
        _ => (null, AppError.Invalid("Status", "Use all, nursing, completed, closed or cancelled.")),
    };

    private static NurseryBatchSort SortOf(string? sortBy) => sortBy?.Trim().ToLowerInvariant() switch
    {
        "startdate" => NurseryBatchSort.StartDate,
        "quantityin" => NurseryBatchSort.QuantityIn,
        "status" => NurseryBatchSort.Status,
        _ => NurseryBatchSort.Code,
    };
}
