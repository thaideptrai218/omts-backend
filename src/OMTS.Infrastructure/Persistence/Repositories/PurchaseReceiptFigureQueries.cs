using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence.Queries;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// PURCHASE-03 read side: money figures are summed by SQL and paged or grouped in the database, so a list, its totals and the
/// export never materialize receipts with their lines and returns. The item group, item and raft
/// filters are line filters (PURCHASE-03 step 3).
/// </summary>
internal sealed partial class PurchaseReceiptRepository
{
    public async Task<(IReadOnlyList<ReceiptFigureRow> Items, ReceiptListTotals Totals)> PageFiguresAsync(
        PurchaseReceiptFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var totals = await TotalsAsync(filter, cancellationToken);
        if (totals.Count == 0)
        {
            return ([], totals);
        }

        var items = await Figures(Sorted(Filtered(filter, db.PurchaseReceipts.AsNoTracking()), filter).Skip((page - 1) * pageSize).Take(pageSize), filter)
            .ToListAsync(cancellationToken);
        return (items, totals);
    }

    public async Task<IReadOnlyList<ReceiptFigureRow>> ListFiguresAsync(PurchaseReceiptFilter filter, int? take, CancellationToken cancellationToken)
    {
        var query = Sorted(Filtered(filter, db.PurchaseReceipts.AsNoTracking()), filter);
        return await Figures(take is { } limit ? query.Take(limit) : query, filter).ToListAsync(cancellationToken);
    }

    public async Task<ReceiptListTotals> TotalsAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken)
    {
        var matching = Filtered(filter, db.PurchaseReceipts.AsNoTracking());
        var count = await matching.CountAsync(cancellationToken);
        var completedCount = await matching.CountAsync(r => r.StatusId == StatusIds.PurchaseReceipt.Completed, cancellationToken);
        var (receipts, returns) = await SumsAsync(filter, cancellationToken);
        return new ReceiptListTotals(count, completedCount, receipts, returns);
    }

    /// <summary>
    /// Σ of the matching lines of the matching Completed receipts received in the period, and Σ of the Recorded returns of those
    /// lines, dated in the period whatever the date received (PURCHASE-03 step 3, BR-FARM-05).
    /// </summary>
    private async Task<(decimal Receipts, decimal Returns)> SumsAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken)
    {
        if ((filter.StatusId is { } statusId && statusId != StatusIds.PurchaseReceipt.Completed)
            || (filter.StatusIds is { } statusIds && !statusIds.Contains(StatusIds.PurchaseReceipt.Completed)))
        {
            // Totals count Completed receipts only (BR-PURCHASE-03), and only Completed receipts have returns.
            return (0, 0);
        }

        var (from, to) = (filter.From ?? DateOnly.MinValue, filter.To ?? DateOnly.MaxValue);
        var lines = OfCompletedReceipts(filter, MatchingLines(filter, db.PurchaseReceiptLines.AsNoTracking()));
        var receipts = await lines.Where(l => l.Receipt.ReceiptDate >= from && l.Receipt.ReceiptDate <= to)
            .SumAsync(l => (decimal?)l.LineAmount, cancellationToken) ?? 0m;
        var lineIds = lines.Select(l => l.Id);
        var returns = await db.RecordedReturnLines()
            .Where(rl => rl.ReceiptLineId != null && lineIds.Contains(rl.ReceiptLineId.Value) && rl.Return.ReturnDate >= from && rl.Return.ReturnDate <= to)
            .SumAsync(rl => (decimal?)rl.ReturnValue, cancellationToken) ?? 0m;
        return (receipts, returns);
    }

    private static IQueryable<PurchaseReceipt> Sorted(IQueryable<PurchaseReceipt> rows, PurchaseReceiptFilter filter)
    {
        var descending = filter.Descending;
        var sorted = filter.Sort switch
        {
            PurchaseReceiptSort.Code => descending ? rows.OrderByDescending(r => r.Code) : rows.OrderBy(r => r.Code),
            PurchaseReceiptSort.Supplier => descending ? rows.OrderByDescending(r => r.Supplier.Name) : rows.OrderBy(r => r.Supplier.Name),
            PurchaseReceiptSort.Total => descending
                ? rows.OrderByDescending(r => r.Lines.Sum(l => (decimal?)l.LineAmount) ?? 0m)
                : rows.OrderBy(r => r.Lines.Sum(l => (decimal?)l.LineAmount) ?? 0m),
            PurchaseReceiptSort.Status => descending ? rows.OrderByDescending(r => r.StatusId) : rows.OrderBy(r => r.StatusId),
            _ => descending ? rows.OrderByDescending(r => r.ReceiptDate) : rows.OrderBy(r => r.ReceiptDate),
        };
        return sorted.ThenByDescending(r => r.Code);
    }

    /// <summary>The receipt with its total and the total of the lines matching the filter, as SQL subqueries.</summary>
    private static IQueryable<ReceiptFigureRow> Figures(IQueryable<PurchaseReceipt> receipts, PurchaseReceiptFilter? filter)
    {
        var (group, itemId, raftId, groups) = (filter?.ItemGroup, filter?.ItemId, filter?.RaftId, filter?.ItemGroups);
        return receipts.Select(r => new ReceiptFigureRow(
            r.Id, r.Code, r.ReceiptDate, r.SupplierId, r.Supplier.Name, r.Supplier.PhoneNumber, r.StatusId, r.RecordedByAccountId,
            r.Lines.Sum(l => (decimal?)l.LineAmount) ?? 0m,
            r.Lines.Where(l => (group == null || (l.Item != null && l.Item.Group == group)) && (groups == null || (l.Item != null && groups.Contains(l.Item.Group)))
                    && (itemId == null || l.ItemId == itemId) && (raftId == null || l.RaftId == raftId))
                .Sum(l => (decimal?)l.LineAmount) ?? 0m,
            r.Supplier.Code,
            r.Lines.Count,
            (r.Returns.Where(x => x.StatusId == StatusIds.PurchaseReturn.Recorded).SelectMany(x => x.Lines).Sum(l => (decimal?)l.ReturnValue) ?? 0m)
                - (r.Returns.Where(x => x.StatusId == StatusIds.PurchaseReturn.Recorded).Sum(x => (decimal?)x.Discount) ?? 0m),
            r.Discount, r.ZoneId, r.Zone!.Code, r.Zone!.Name, r.Notes, r.CompletedAt, r.CreatedAt, r.UpdatedAt, r.ReceivedAt, r.ReceivedByAccountId,
            // Codes of its Recorded returns, oldest first, for the list's "Mã trả hàng nhập" column.
            r.Returns.Where(x => x.StatusId == StatusIds.PurchaseReturn.Recorded).OrderBy(x => x.ReturnedAt).Select(x => x.Code).ToList()));
    }

    private static IQueryable<PurchaseReceiptLine> MatchingLines(PurchaseReceiptFilter filter, IQueryable<PurchaseReceiptLine> lines)
    {
        if (filter.ItemGroup is { } group)
        {
            lines = lines.Where(l => l.Item != null && l.Item.Group == group);
        }

        if (filter.ItemGroups is { } groups)
        {
            lines = lines.Where(l => l.Item != null && groups.Contains(l.Item.Group));
        }

        if (filter.ItemId is { } itemId)
        {
            lines = lines.Where(l => l.ItemId == itemId);
        }

        if (filter.RaftId is { } raftId)
        {
            lines = lines.Where(l => l.RaftId == raftId);
        }

        return lines;
    }

    /// <summary>Lines of Completed receipts that match the supplier and code filters (not the period).</summary>
    private static IQueryable<PurchaseReceiptLine> OfCompletedReceipts(PurchaseReceiptFilter filter, IQueryable<PurchaseReceiptLine> lines)
    {
        lines = lines.Where(l => l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed);
        if (filter.SupplierId is { } supplierId)
        {
            lines = lines.Where(l => l.Receipt.SupplierId == supplierId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            var code = filter.Code.Trim();
            lines = lines.Where(l => l.Receipt.Code.Contains(code));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            lines = lines.Where(l => l.Receipt.Code.Contains(text) || l.Receipt.Supplier.Name.Contains(text));
        }

        if (filter.ZoneIds is { } zoneIds)
        {
            lines = lines.Where(l => l.Receipt.ZoneId != null && zoneIds.Contains(l.Receipt.ZoneId.Value));
        }

        return lines;
    }

    private static IQueryable<PurchaseReceipt> Filtered(PurchaseReceiptFilter filter, IQueryable<PurchaseReceipt> query)
    {
        if (filter.ItemGroup is not null || filter.ItemGroups is not null || filter.ItemId is not null || filter.RaftId is not null)
        {
            var (group, groups, itemId, raftId) = (filter.ItemGroup, filter.ItemGroups, filter.ItemId, filter.RaftId);
            query = query.Where(r => r.Lines.Any(l =>
                (group == null || (l.Item != null && l.Item.Group == group)) && (groups == null || (l.Item != null && groups.Contains(l.Item.Group)))
                && (itemId == null || l.ItemId == itemId) && (raftId == null || l.RaftId == raftId)));
        }

        if (filter.SupplierId is { } supplierId)
        {
            query = query.Where(r => r.SupplierId == supplierId);
        }

        if (filter.StatusId is { } statusId)
        {
            query = query.Where(r => r.StatusId == statusId);
        }

        if (filter.StatusIds is { } statusIds)
        {
            query = query.Where(r => statusIds.Contains(r.StatusId));
        }

        if (filter.ZoneIds is { } zoneIds)
        {
            query = query.Where(r => r.ZoneId != null && zoneIds.Contains(r.ZoneId.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            query = query.Where(r => r.Code.Contains(text) || r.Supplier.Name.Contains(text));
        }

        if (filter.From is { } from)
        {
            query = query.Where(r => r.ReceiptDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => r.ReceiptDate <= to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            var code = filter.Code.Trim();
            query = query.Where(r => r.Code.Contains(code));
        }

        return query;
    }
}
