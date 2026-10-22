namespace OMTS.Application.Common.Interfaces;

/// <summary>
/// Material cost of rafts = total of the purchase receipts linked to them (FARM-04, INP-03). Until purchase
/// receipts exist the answer is "not available" (null), which the raft detail shows as no value, never 0.
/// </summary>
public interface IRaftMaterialCostQuery
{
    /// <summary>VND per raft id, or null when the cost cannot be known yet.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>?> GetTotalsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken);
}
