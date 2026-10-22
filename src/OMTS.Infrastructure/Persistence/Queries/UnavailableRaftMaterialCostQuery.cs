using OMTS.Application.Common.Interfaces;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>Placeholder until purchase receipts are built (INP ticket): the raft material cost is not available.</summary>
internal sealed class UnavailableRaftMaterialCostQuery : IRaftMaterialCostQuery
{
    public Task<IReadOnlyDictionary<Guid, decimal>?> GetTotalsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, decimal>?>(null);
}
