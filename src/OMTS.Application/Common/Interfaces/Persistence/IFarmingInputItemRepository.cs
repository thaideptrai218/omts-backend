using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IFarmingInputItemRepository
{
    Task<FarmingInputItem?> GetForEditAsync(Guid itemId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FarmingInputItem>> GetByIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>Another item whose code equals <paramref name="code"/> ignoring case, discontinued items included.</summary>
    Task<FarmingInputItem?> FindByCodeAsync(string code, Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>Another item whose name equals <paramref name="name"/> ignoring case, discontinued items included.</summary>
    Task<FarmingInputItem?> FindByNameAsync(string name, Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>
    /// The unit of the Seed items other than <paramref name="exceptItemId"/>, discontinued ones included: the unit of the
    /// first one created, or null when there is no other Seed item (BR-PURCHASE-10).
    /// </summary>
    Task<string?> SeedUnitAsync(Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>Every item code that starts with <paramref name="prefix"/> ignoring case, discontinued items included.</summary>
    Task<IReadOnlyList<string>> CodesStartingWithAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>By code or name (contains), optional group and status, ordered by code.</summary>
    Task<IReadOnlyList<FarmingInputItem>> ListAsync(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken);

    /// <summary>True when the item is on a receipt line or is the planned seed of a cultivation cycle.</summary>
    Task<bool> IsUsedAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Which of the items are used by a receipt line or a cycle plan (two grouped queries for a whole list).</summary>
    Task<IReadOnlySet<Guid>> UsedAmongAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    void Add(FarmingInputItem item);
}
