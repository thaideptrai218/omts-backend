using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IFarmingInputItemRepository
{
    Task<FarmingInputItem?> GetForEditAsync(Guid itemId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FarmingInputItem>> GetByIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>Another item whose code equals <paramref name="code"/> ignoring case.</summary>
    Task<FarmingInputItem?> FindByCodeAsync(string code, Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>By code or name (contains), optional group and status, ordered by code.</summary>
    Task<IReadOnlyList<FarmingInputItem>> ListAsync(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken);

    /// <summary>True when the item is on a receipt line or is the planned seed of a cultivation cycle.</summary>
    Task<bool> IsUsedAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Which of the items are used by a receipt line or a cycle plan (two grouped queries for a whole list).</summary>
    Task<IReadOnlySet<Guid>> UsedAmongAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    void Add(FarmingInputItem item);
}
