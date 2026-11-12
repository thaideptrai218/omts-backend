using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class InputIssueRepository(OmtsDbContext db) : IInputIssueRepository
{
    public Task<InputIssue?> GetForEditAsync(Guid issueId, CancellationToken cancellationToken) =>
        db.InputIssues
            .Include(i => i.Zone)
            .Include(i => i.Raft)
            .Include(i => i.IssueType)
            .Include(i => i.Receiver)
            .Include(i => i.Lines).ThenInclude(l => l.Item).ThenInclude(i => i.Units).ThenInclude(u => u.Unit)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == issueId, cancellationToken);

    public Task<InputIssue?> FindByCodeAsync(string code, Guid? exceptIssueId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.InputIssues.AsNoTracking().FirstOrDefaultAsync(i => i.Code.ToUpper() == upper && i.Id != exceptIssueId, cancellationToken);
    }

    public Task<bool> ReceiverWorksInAsync(Guid employeeId, Guid zoneId, CancellationToken cancellationToken) =>
        db.EmployeeWorkingZones.AnyAsync(w => w.EmployeeId == employeeId && w.ZoneId == zoneId && w.Employee.StatusId == StatusIds.Employee.Working, cancellationToken);

    public async Task<IReadOnlyList<InputIssueReceiver>> ReceiversAsync(Guid zoneId, CancellationToken cancellationToken) =>
        await db.EmployeeWorkingZones.AsNoTracking().Where(w => w.ZoneId == zoneId && w.Employee.StatusId == StatusIds.Employee.Working)
            .OrderBy(w => w.Employee.FullName).Select(w => new InputIssueReceiver(w.EmployeeId, w.Employee.FullName)).ToListAsync(cancellationToken);

    public async Task<bool> IsLotOfItemAsync(Guid itemId, Guid receiptLineId, CancellationToken cancellationToken) =>
        await db.PurchaseReceiptLines.AnyAsync(
            l => l.Id == receiptLineId && l.ItemId == itemId && l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed, cancellationToken);

    public async Task<(IReadOnlyList<InputIssueRow> Rows, int Total, decimal TotalValue)> SearchAsync(
        InputIssueFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        var ids = await Sorted(query, filter).Skip((page - 1) * pageSize).Take(pageSize).Select(f => f.Id).ToListAsync(cancellationToken);
        // The total value counts the Completed issues only; SQL Server cannot sum an expression that holds a subquery, so the values are read and added up here.
        var values = total == 0
            ? []
            : await query.Where(f => f.StatusId == StatusIds.InputIssue.Completed).Select(f => f.TotalValue).ToListAsync(cancellationToken);
        return (await RowsAsync(ids, cancellationToken), total, values.Sum());
    }

    public async Task<IReadOnlyList<InputIssueRow>> ListAsync(InputIssueFilter filter, CancellationToken cancellationToken) =>
        await RowsAsync(await Sorted(Filtered(filter), filter).Select(f => f.Id).ToListAsync(cancellationToken), cancellationToken);

    public void Add(InputIssue issue) => db.InputIssues.Add(issue);

    public void Remove(InputIssue issue) => db.InputIssues.Remove(issue);

    public void AddLine(InputIssueLine line) => db.InputIssueLines.Add(line);

    public void RemoveLine(InputIssueLine line) => db.InputIssueLines.Remove(line);

    private sealed class Figures
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public DateTimeOffset IssuedAt { get; init; }
        public int StatusId { get; init; }
        public decimal TotalValue { get; init; }
    }

    private async Task<IReadOnlyList<InputIssueRow>> RowsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var issues = (await db.InputIssues.AsNoTracking()
            .Include(i => i.Zone).Include(i => i.Raft).Include(i => i.IssueType).Include(i => i.Receiver)
            .Where(i => ids.Contains(i.Id)).AsSplitQuery().ToListAsync(cancellationToken)).ToDictionary(i => i.Id);
        var sums = await db.InputIssueLines.AsNoTracking().Where(l => ids.Contains(l.IssueId))
            .GroupBy(l => l.IssueId).Select(g => new { g.Key, Sum = g.Sum(l => l.Value) }).ToListAsync(cancellationToken);
        return ids.Select(id => new InputIssueRow(issues[id], sums.FirstOrDefault(s => s.Key == id)?.Sum ?? 0m)).ToList();
    }

    private IQueryable<Figures> Filtered(InputIssueFilter filter)
    {
        var issues = db.InputIssues.AsNoTracking().AsQueryable();
        if (filter.Kind is { } kind)
        {
            issues = issues.Where(i => i.Kind == kind);
        }

        // What the caller may see: the uses of the zones it has (all of them when null) and the disposals when it may read them.
        var zones = filter.UseZoneIds?.ToList();
        issues = (filter.IncludeUse, filter.IncludeDisposals) switch
        {
            (true, true) => zones is null ? issues : issues.Where(i => i.Kind == InputIssueKind.Disposal || (i.ZoneId != null && zones.Contains(i.ZoneId.Value))),
            (true, false) => zones is null ? issues.Where(i => i.Kind == InputIssueKind.Use) : issues.Where(i => i.Kind == InputIssueKind.Use && i.ZoneId != null && zones.Contains(i.ZoneId.Value)),
            (false, true) => issues.Where(i => i.Kind == InputIssueKind.Disposal),
            _ => issues.Where(i => false),
        };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            issues = issues.Where(i => i.Code.Contains(text) || (i.Notes != null && i.Notes.Contains(text)) || (i.Reason != null && i.Reason.Contains(text)));
        }

        if (filter.StatusIds is { Count: > 0 } statusIds)
        {
            issues = issues.Where(i => statusIds.Contains(i.StatusId));
        }

        if (filter.From is { } from)
        {
            issues = issues.Where(i => i.IssuedAt >= from);
        }

        if (filter.Before is { } before)
        {
            issues = issues.Where(i => i.IssuedAt < before);
        }

        if (filter.ZoneId is { } zoneId)
        {
            issues = issues.Where(i => i.ZoneId == zoneId);
        }

        if (filter.RaftId is { } raftId)
        {
            issues = issues.Where(i => i.RaftId == raftId);
        }

        if (filter.IssueTypeId is { } typeId)
        {
            issues = issues.Where(i => i.IssueTypeId == typeId);
        }

        if (filter.CreatedById is { } createdBy)
        {
            issues = issues.Where(i => i.CreatedBy == createdBy);
        }

        if (filter.ReceiverId is { } receiverId)
        {
            issues = issues.Where(i => i.ReceiverEmployeeId == receiverId);
        }

        return issues.Select(i => new Figures
        {
            Id = i.Id, Code = i.Code, IssuedAt = i.IssuedAt, StatusId = i.StatusId,
            TotalValue = db.InputIssueLines.Where(l => l.IssueId == i.Id).Sum(l => (decimal?)l.Value) ?? 0m,
        });
    }

    private static IQueryable<Figures> Sorted(IQueryable<Figures> query, InputIssueFilter filter)
    {
        var descending = filter.Descending;
        IOrderedQueryable<Figures> Order<TKey>(System.Linq.Expressions.Expression<Func<Figures, TKey>> key) =>
            descending ? query.OrderByDescending(key) : query.OrderBy(key);
        var ordered = filter.Sort switch
        {
            InputIssueSort.IssuedAt => Order(f => f.IssuedAt),
            InputIssueSort.TotalValue => Order(f => f.TotalValue),
            InputIssueSort.Status => Order(f => f.StatusId),
            _ => Order(f => f.Code),
        };
        return ordered.ThenBy(f => f.Id);
    }
}

internal sealed class InputIssueTypeRepository(OmtsDbContext db) : IInputIssueTypeRepository
{
    public async Task<IReadOnlyList<InputIssueType>> ListActiveAsync(CancellationToken cancellationToken) =>
        await db.InputIssueTypes.AsNoTracking().Where(t => t.IsActive).OrderByDescending(t => t.IsSystem).ThenBy(t => t.CreatedAt).ThenBy(t => t.Name).ToListAsync(cancellationToken);

    public Task<InputIssueType?> GetForEditAsync(Guid typeId, CancellationToken cancellationToken) =>
        db.InputIssueTypes.FirstOrDefaultAsync(t => t.Id == typeId, cancellationToken);

    public Task<InputIssueType?> FindByNameAsync(string name, Guid? exceptTypeId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.InputIssueTypes.AsNoTracking().FirstOrDefaultAsync(t => t.IsActive && t.Name.ToUpper() == upper && t.Id != exceptTypeId, cancellationToken);
    }

    public Task<bool> IsUsedAsync(Guid typeId, CancellationToken cancellationToken) =>
        db.InputIssues.AnyAsync(i => i.IssueTypeId == typeId, cancellationToken);

    public void Add(InputIssueType type) => db.InputIssueTypes.Add(type);

    public void Remove(InputIssueType type) => db.InputIssueTypes.Remove(type);
}

internal sealed class InputMovementReader(OmtsDbContext db) : IInputMovementQuery
{
    public async Task<IReadOnlyList<InputMovementRow>> ForItemAsync(Guid itemId, bool includeReversals, CancellationToken cancellationToken)
    {
        var rows = await db.InputStockMovements.AsNoTracking().Where(m => m.ItemId == itemId)
            .OrderBy(m => m.OccurredAt).ThenBy(m => m.Sequence).ToListAsync(cancellationToken);
        if (!includeReversals)
        {
            // An edit or cancellation undoes rows with reversal rows: both sides are left out, so the running balance still adds up.
            var undone = rows.Where(m => m.ReversesMovementId != null).Select(m => m.ReversesMovementId!.Value).ToHashSet();
            rows = rows.Where(m => m.Kind != InputStockMovementKind.Reversal && !undone.Contains(m.Id)).ToList();
        }

        var lotIds = rows.Where(m => m.SeedLotLineId != null).Select(m => m.SeedLotLineId!.Value).Distinct().ToList();
        var codes = lotIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await db.PurchaseReceiptLines.AsNoTracking().Where(l => lotIds.Contains(l.Id)).Select(l => new { l.Id, ReceiptCode = l.Receipt.Code, l.LineNumber }).ToListAsync(cancellationToken))
                .ToDictionary(r => r.Id, r => $"{r.ReceiptCode}-{r.LineNumber}");
        var balance = 0m;
        return rows.Select(m =>
        {
            balance += m.Quantity;
            return new InputMovementRow(m, balance, m.SeedLotLineId is { } lot ? codes.GetValueOrDefault(lot) : null);
        }).ToList();
    }

    public async Task<IReadOnlyList<InputSeedLotRow>> SeedLotsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var lots = await db.PurchaseReceiptLines.AsNoTracking()
            .Where(l => l.ItemId == itemId && l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed)
            .Select(l => new
            {
                l.Id, l.LineNumber, ReceiptId = l.Receipt.Id, ReceiptCode = l.Receipt.Code, l.Receipt.ReceivedAt, SupplierName = l.Receipt.Supplier.Name, l.SupplierLotCode, Received = l.Quantity ?? 0m,
            })
            .OrderBy(l => l.ReceivedAt).ThenBy(l => l.LineNumber).ToListAsync(cancellationToken);
        var ids = lots.Select(l => l.Id).ToList();
        var sums = ids.Count == 0
            ? []
            : await db.InputStockMovements.AsNoTracking().Where(m => m.ItemId == itemId && m.SeedLotLineId != null && ids.Contains(m.SeedLotLineId.Value))
                .GroupBy(m => m.SeedLotLineId!.Value).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToListAsync(cancellationToken);
        return lots.Select(l => new InputSeedLotRow(
            l.Id, $"{l.ReceiptCode}-{l.LineNumber}", l.ReceiptId, l.ReceiptCode, l.ReceivedAt, l.SupplierName, l.SupplierLotCode, l.Received, sums.FirstOrDefault(s => s.Key == l.Id)?.Sum ?? 0m)).ToList();
    }

    public async Task<InputSeedLotRow?> SeedLotAsync(Guid itemId, Guid lotLineId, CancellationToken cancellationToken) =>
        (await SeedLotsAsync(itemId, cancellationToken)).FirstOrDefault(l => l.LineId == lotLineId);
}
