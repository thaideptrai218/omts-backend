using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing.SeedLots;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The seed lot register read side (a seed lot is a Seed line of a Completed receipt). Every quantity of a lot is a SQL subquery in the one select that reads the lots, so the list, one lot and the lines of a
/// receipt cost one round trip each whatever the number of lots; the state is derived from the quantities by the application.
/// </summary>
internal sealed class SeedLotRegisterQuery(OmtsDbContext db) : ISeedLotRegisterQuery
{
    /// <summary>One lot as the select reads it, before the latest use is taken from the three places a lot is used.</summary>
    private sealed record Raw(
        Guid LineId, int LineNumber, Guid ReceiptId, string ReceiptCode, DateTimeOffset ReceivedAt, Guid SupplierId, string SupplierCode, string SupplierName,
        Guid ItemId, string ItemCode, string ItemName, Guid? ProductId, string? ProductCode, string? ProductName, string UnitName,
        decimal Received, decimal Returned, decimal NurseryIn, decimal NurseryLoss, decimal SeededViaNursery, decimal SeededDirect, decimal IssuedOther,
        DateOnly? LastSeeding, DateOnly? LastNursery, DateTimeOffset? LastIssue);

    public async Task<IReadOnlyList<SeedLotFiguresRow>> SearchAsync(SeedLotRegisterFilter filter, CancellationToken cancellationToken)
    {
        var lines = SeedLotLines();
        if (filter.From is { } from)
        {
            lines = lines.Where(l => l.Receipt.ReceiptDate >= from);
        }

        if (filter.To is { } to)
        {
            lines = lines.Where(l => l.Receipt.ReceiptDate <= to);
        }

        if (filter.SupplierId is { } supplierId)
        {
            lines = lines.Where(l => l.Receipt.SupplierId == supplierId);
        }

        if (filter.ItemId is { } itemId)
        {
            lines = lines.Where(l => l.ItemId == itemId);
        }

        if (filter.ZoneId is { } zoneId)
        {
            lines = lines.Where(l => l.Receipt.ZoneId == zoneId
                || db.NurseryBatches.Any(b => b.SeedLotLineId == l.Id && b.ZoneId == zoneId)
                || db.SeedingRecords.Any(s => s.SeedLotLineId == l.Id && s.Cycle.Raft.ZoneId == zoneId));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            lines = lines.Where(l => l.Receipt.Code.Contains(text)
                || (l.Receipt.Code + "-" + l.LineNumber.ToString()).Contains(text)
                || (l.SupplierLotCode != null && l.SupplierLotCode.Contains(text))
                || l.Item!.Code.Contains(text) || l.Item.Name.Contains(text));
        }

        var raws = await Project(lines.OrderByDescending(l => l.Receipt.ReceivedAt).ThenByDescending(l => l.Receipt.Code).ThenBy(l => l.LineNumber)).ToListAsync(cancellationToken);
        return raws.Select(Row).ToList();
    }

    public async Task<SeedLotFiguresRow?> GetAsync(Guid lineId, CancellationToken cancellationToken) =>
        await Project(SeedLotLines().Where(l => l.Id == lineId)).ToListAsync(cancellationToken) is [var raw] ? Row(raw) : null;

    public async Task<IReadOnlyDictionary<Guid, SeedLotFigures>> FiguresAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        if (lineIds.Count == 0)
        {
            return new Dictionary<Guid, SeedLotFigures>();
        }

        var ids = lineIds.ToList();
        var raws = await Project(SeedLotLines().Where(l => ids.Contains(l.Id))).ToListAsync(cancellationToken);
        return raws.Select(Row).ToDictionary(r => r.LineId, r => r.Figures);
    }

    public async Task<ILookup<Guid, SeedLotFigures>> FiguresOfReceiptsAsync(IReadOnlyCollection<Guid> receiptIds, CancellationToken cancellationToken)
    {
        if (receiptIds.Count == 0)
        {
            return Enumerable.Empty<SeedLotFiguresRow>().ToLookup(r => r.ReceiptId, r => r.Figures);
        }

        var ids = receiptIds.ToList();
        var raws = await Project(SeedLotLines().Where(l => ids.Contains(l.ReceiptId))).ToListAsync(cancellationToken);
        return raws.Select(Row).ToLookup(r => r.ReceiptId, r => r.Figures);
    }

    public async Task<SeedLotDetailRows> DetailAsync(Guid lineId, CancellationToken cancellationToken)
    {
        var batches = await db.NurseryBatches.AsNoTracking().Where(b => b.SeedLotLineId == lineId)
            .Select(b => new SeedLotNurseryBatchRow(b.Id, b.Code, b.StatusId, b.QuantityIn, b.Survivors, b.StartDate)).ToListAsync(cancellationToken);
        var seedings = await db.SeedingRecords.AsNoTracking().Where(s => s.SeedLotLineId == lineId)
            .Select(s => new SeedLotSeedingRow(s.Id, s.CycleId, s.Cycle.Raft.Code, s.Cycle.Raft.Zone.Code, s.Quantity, s.SeedingDate)).ToListAsync(cancellationToken);
        var returns = await db.PurchaseReturnLines.AsNoTracking().Where(r => r.ReceiptLineId == lineId && r.Return.StatusId != StatusIds.PurchaseReturn.Draft)
            .Select(r => new SeedLotReturnRow(r.Return.Id, r.Return.Code, r.Quantity, r.Return.ReturnedAt, r.Return.StatusId)).ToListAsync(cancellationToken);
        var issues = await db.InputIssueLines.AsNoTracking().Where(i => i.SeedLotLineId == lineId && i.Issue.StatusId == StatusIds.InputIssue.Completed && i.Issue.SourceKind == InputIssueSource.None)
            .Select(i => new SeedLotIssueRow(i.Issue.Id, i.Issue.Code, i.Issue.Kind, i.Quantity, i.Issue.IssuedAt)).ToListAsync(cancellationToken);
        var cycles = await db.CultivationCycles.AsNoTracking().Where(c => db.SeedingRecords.Any(s => s.CycleId == c.Id && s.SeedLotLineId == lineId))
            .OrderBy(c => c.StartDate)
            .Select(c => new SeedLotCycleRow(c.Id, c.Raft.Code, c.Raft.Zone.Code, c.StatusId, c.Product != null ? c.Product.Code : null)).ToListAsync(cancellationToken);
        var harvestLots = await db.HarvestLots.AsNoTracking().Where(h => db.SeedingRecords.Any(s => s.CycleId == h.CycleId && s.SeedLotLineId == lineId))
            .OrderBy(h => h.HarvestedAt)
            .Select(h => new SeedLotHarvestLotRow(h.Id, h.LotCode, h.StatusId, h.WeighedKg, h.EstimatedKg)).ToListAsync(cancellationToken);
        return new SeedLotDetailRows(batches, seedings, returns, issues, cycles, harvestLots);
    }

    public async Task<SeedLotHarvestSources> HarvestSourcesAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        if (lineIds.Count == 0)
        {
            return SeedLotHarvestSources.Empty;
        }

        var ids = lineIds.ToList();
        var cycleIds = db.SeedingRecords.Where(s => ids.Contains(s.SeedLotLineId)).Select(s => s.CycleId).Distinct();
        var seedings = await db.SeedingRecords.AsNoTracking().Where(s => cycleIds.Contains(s.CycleId))
            .Select(s => new SeedLotCycleSeeding(s.CycleId, s.SeedLotLineId, s.Quantity)).ToListAsync(cancellationToken);
        var harvestLots = await db.HarvestLots.AsNoTracking().Where(h => cycleIds.Contains(h.CycleId))
            .Select(h => new SeedLotCycleHarvest(h.CycleId, h.Id, h.EstimatedKg, h.WeighedKg)).ToListAsync(cancellationToken);
        return new SeedLotHarvestSources(seedings, harvestLots);
    }

    /// <summary>The Seed lines of Completed receipts: the seed lots (GBR-04).</summary>
    private IQueryable<PurchaseReceiptLine> SeedLotLines() =>
        db.PurchaseReceiptLines.AsNoTracking().Where(l => l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed && l.Item != null && l.Item.Group == InputItemGroup.Seed);

    private IQueryable<Raw> Project(IQueryable<PurchaseReceiptLine> lines) =>
        lines.Select(l => new Raw(
            l.Id, l.LineNumber, l.ReceiptId, l.Receipt.Code, l.Receipt.ReceivedAt, l.Receipt.SupplierId, l.Receipt.Supplier.Code, l.Receipt.Supplier.Name,
            l.Item!.Id, l.Item.Code, l.Item.Name, l.Item.ProductId, l.Item.Product != null ? l.Item.Product.Code : null, l.Item.Product != null ? l.Item.Product.Name : null,
            // Seeds are counted in the base unit of the item, whatever unit the line was entered in.
            l.Item.Unit,
            l.Quantity ?? 0m,
            db.PurchaseReturnLines.Where(r => r.ReceiptLineId == l.Id && r.Return.StatusId == StatusIds.PurchaseReturn.Recorded).Sum(r => (decimal?)r.Quantity) ?? 0m,
            // A Cancelled batch gave its pieces back to the stock (its issue was cancelled with it).
            db.NurseryBatches.Where(b => b.SeedLotLineId == l.Id && b.StatusId != StatusIds.NurseryBatch.Cancelled).Sum(b => (decimal?)b.QuantityIn) ?? 0m,
            // What did not survive, plus what the close wrote off the pool of a Closed batch.
            db.NurseryBatches.Where(b => b.SeedLotLineId == l.Id && (b.StatusId == StatusIds.NurseryBatch.Completed || b.StatusId == StatusIds.NurseryBatch.Closed))
                .Sum(b => (decimal?)(b.QuantityIn - (b.Survivors ?? b.QuantityIn) + (b.StatusId == StatusIds.NurseryBatch.Closed ? b.CloseLossPieces ?? 0m : 0m))) ?? 0m,
            db.SeedingRecords.Where(s => s.SeedLotLineId == l.Id && s.NurseryBatchId != null).Sum(s => (decimal?)s.Quantity) ?? 0m,
            db.SeedingRecords.Where(s => s.SeedLotLineId == l.Id && s.NurseryBatchId == null).Sum(s => (decimal?)s.Quantity) ?? 0m,
            // A seeding or nursery issue belongs to its own record; the manual uses and disposals are the rest.
            db.InputIssueLines.Where(i => i.SeedLotLineId == l.Id && i.Issue.StatusId == StatusIds.InputIssue.Completed && i.Issue.SourceKind == InputIssueSource.None)
                .Sum(i => (decimal?)i.Quantity) ?? 0m,
            db.SeedingRecords.Where(s => s.SeedLotLineId == l.Id).Max(s => (DateOnly?)s.SeedingDate),
            db.NurseryBatches.Where(b => b.SeedLotLineId == l.Id && b.StatusId != StatusIds.NurseryBatch.Cancelled).Max(b => (DateOnly?)b.StartDate),
            db.InputIssueLines.Where(i => i.SeedLotLineId == l.Id && i.Issue.StatusId == StatusIds.InputIssue.Completed && i.Issue.SourceKind == InputIssueSource.None)
                .Max(i => (DateTimeOffset?)i.Issue.IssuedAt)));

    private static SeedLotFiguresRow Row(Raw r)
    {
        // A session or a nursery batch records a day, an issue a time: the latest of them, a day counting from its start.
        var used = new[]
        {
            r.LastSeeding is { } seeding ? BusinessClock.FromLocal(seeding.ToDateTime(TimeOnly.MinValue)) : (DateTimeOffset?)null,
            r.LastNursery is { } nursery ? BusinessClock.FromLocal(nursery.ToDateTime(TimeOnly.MinValue)) : null,
            r.LastIssue,
        };
        var figures = new SeedLotFigures(r.Received, r.Returned, r.NurseryIn, r.NurseryLoss, r.SeededViaNursery, r.SeededDirect, r.IssuedOther, used.Max());
        return new SeedLotFiguresRow(
            r.LineId, r.LineNumber, r.ReceiptId, r.ReceiptCode, r.ReceivedAt, r.SupplierId, r.SupplierCode, r.SupplierName, r.ItemId, r.ItemCode, r.ItemName,
            r.ProductId, r.ProductCode, r.ProductName, r.UnitName, figures);
    }
}
