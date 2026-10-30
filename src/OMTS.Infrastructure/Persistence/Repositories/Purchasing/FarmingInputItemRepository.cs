using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories.Purchasing;

internal sealed class FarmingInputItemRepository(OmtsDbContext db) : IFarmingInputItemRepository
{
    public Task<FarmingInputItem?> GetForEditAsync(Guid itemId, CancellationToken cancellationToken) =>
        db.FarmingInputItems.FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);

    public async Task<IReadOnlyList<FarmingInputItem>> GetByIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        await db.FarmingInputItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToListAsync(cancellationToken);

    public Task<FarmingInputItem?> FindByCodeAsync(string code, Guid? exceptItemId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.FarmingInputItems.AsNoTracking().FirstOrDefaultAsync(i => i.Code.ToUpper() == upper && i.Id != exceptItemId, cancellationToken);
    }

    public async Task<IReadOnlyList<FarmingInputItem>> ListAsync(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken)
    {
        var query = db.FarmingInputItems.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(i => i.Code.Contains(text) || i.Name.Contains(text));
        }

        if (group is not null)
        {
            query = query.Where(i => i.Group == group);
        }

        if (statusId is not null)
        {
            query = query.Where(i => i.StatusId == statusId);
        }

        return await query.OrderBy(i => i.Code).ToListAsync(cancellationToken);
    }

    public async Task<bool> IsUsedAsync(Guid itemId, CancellationToken cancellationToken) =>
        await db.PurchaseReceiptLines.AnyAsync(l => l.ItemId == itemId, cancellationToken)
        || await db.CultivationCycles.AnyAsync(c => c.PlannedSeedTypeId == itemId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> UsedAmongAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var onReceipts = await db.PurchaseReceiptLines.Where(l => itemIds.Contains(l.ItemId)).Select(l => l.ItemId).Distinct().ToListAsync(cancellationToken);
        var onCycles = await db.CultivationCycles.Where(c => c.PlannedSeedTypeId != null && itemIds.Contains(c.PlannedSeedTypeId.Value))
            .Select(c => c.PlannedSeedTypeId!.Value).Distinct().ToListAsync(cancellationToken);
        return onReceipts.Concat(onCycles).ToHashSet();
    }

    public void Add(FarmingInputItem item) => db.FarmingInputItems.Add(item);
}
