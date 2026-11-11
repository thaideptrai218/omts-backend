using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>The item list: the items that are not deleted, with the quantity on hand summed from the movement ledger by SQL.</summary>
internal sealed class InputItemListReader(OmtsDbContext db) : IInputItemListQuery
{
    private sealed class Figures
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public InputItemGroup Group { get; init; }
        public int StatusId { get; init; }
        public decimal? CostPrice { get; init; }
        public decimal? LastPurchasePrice { get; init; }
        public decimal? MinQuantity { get; init; }
        public decimal? MaxQuantity { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public decimal OnHand { get; init; }
    }

    public async Task<(IReadOnlyList<InputItemRow> Rows, int Total, decimal StockValue)> SearchAsync(
        InputItemFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        var ids = await Sorted(query, filter).Skip((page - 1) * pageSize).Take(pageSize).Select(f => f.Id).ToListAsync(cancellationToken);
        // SQL Server cannot sum an expression that holds a subquery: the figures of each match are read and added up here.
        var figures = total == 0
            ? []
            : await query.Select(f => new { f.OnHand, Cost = f.CostPrice }).ToListAsync(cancellationToken);
        return (await RowsAsync(ids, cancellationToken), total, figures.Sum(f => f.OnHand * (f.Cost ?? 0m)));
    }

    public async Task<IReadOnlyList<InputItemRow>> ListAsync(InputItemFilter filter, CancellationToken cancellationToken) =>
        await RowsAsync(await Sorted(Filtered(filter), filter).Select(f => f.Id).ToListAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<InputItemRow>> RowsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var items = (await db.FarmingInputItems.AsNoTracking()
            .Include(i => i.Units).ThenInclude(u => u.Unit)
            .Include(i => i.ItemGroup)
            .Include(i => i.Photos)
            .Where(i => ids.Contains(i.Id)).AsSplitQuery().ToListAsync(cancellationToken)).ToDictionary(i => i.Id);
        var sums = await db.InputStockMovements.AsNoTracking().Where(m => ids.Contains(m.ItemId))
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToListAsync(cancellationToken);
        return ids.Select(id => new InputItemRow(items[id], sums.FirstOrDefault(s => s.Key == id)?.Sum ?? 0m)).ToList();
    }

    private IQueryable<Figures> Filtered(InputItemFilter filter)
    {
        var items = db.FarmingInputItems.AsNoTracking().Where(i => !i.IsHidden);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            items = items.Where(i => i.Code.Contains(text) || i.Name.Contains(text) || i.Units.Any(u => u.Code.Contains(text)));
        }

        if (filter.Group is { } group)
        {
            items = items.Where(i => i.Group == group);
        }

        if (filter.ItemGroupId is { } itemGroupId)
        {
            // A parent group also lists the items of its child groups.
            items = items.Where(i => i.ItemGroupId == itemGroupId || (i.ItemGroup != null && i.ItemGroup.ParentId == itemGroupId));
        }

        if (filter.StatusId is { } statusId)
        {
            items = items.Where(i => i.StatusId == statusId);
        }

        if (filter.SupplierId is { } supplierId)
        {
            items = items.Where(i => db.PurchaseReceiptLines.Any(
                l => l.ItemId == i.Id && l.Receipt.SupplierId == supplierId && l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed));
        }

        if (filter.CreatedFrom is { } from)
        {
            items = items.Where(i => i.CreatedAt >= from);
        }

        if (filter.CreatedBefore is { } before)
        {
            items = items.Where(i => i.CreatedAt < before);
        }

        var figures = items.Select(i => new Figures
        {
            Id = i.Id, Code = i.Code, Name = i.Name, Group = i.Group, StatusId = i.StatusId, CostPrice = i.CostPrice, LastPurchasePrice = i.LastPurchasePrice,
            MinQuantity = i.MinQuantity, MaxQuantity = i.MaxQuantity, CreatedAt = i.CreatedAt,
            OnHand = db.InputStockMovements.Where(m => m.ItemId == i.Id).Sum(m => (decimal?)m.Quantity) ?? 0m,
        });
        return filter.Stock switch
        {
            InputStockState.InStock => figures.Where(f => f.OnHand > 0),
            InputStockState.OutOfStock => figures.Where(f => f.OnHand <= 0),
            InputStockState.BelowMin => figures.Where(f => f.MinQuantity != null && f.OnHand < f.MinQuantity),
            InputStockState.AboveMax => figures.Where(f => f.MaxQuantity != null && f.OnHand > f.MaxQuantity),
            _ => figures,
        };
    }

    private static IQueryable<Figures> Sorted(IQueryable<Figures> query, InputItemFilter filter)
    {
        var descending = filter.Descending;
        IOrderedQueryable<Figures> Order<TKey>(System.Linq.Expressions.Expression<Func<Figures, TKey>> key) =>
            descending ? query.OrderByDescending(key) : query.OrderBy(key);
        var ordered = filter.Sort switch
        {
            InputItemSort.Name => Order(f => f.Name),
            InputItemSort.Group => Order(f => f.Group),
            InputItemSort.OnHand => Order(f => f.OnHand),
            InputItemSort.CostPrice => Order(f => f.CostPrice),
            InputItemSort.LastPurchasePrice => Order(f => f.LastPurchasePrice),
            InputItemSort.StockValue => Order(f => f.OnHand * (f.CostPrice ?? 0m)),
            InputItemSort.CreatedAt => Order(f => f.CreatedAt),
            InputItemSort.Status => Order(f => f.StatusId),
            _ => Order(f => f.Code),
        };
        return ordered.ThenBy(f => f.Id);
    }
}

