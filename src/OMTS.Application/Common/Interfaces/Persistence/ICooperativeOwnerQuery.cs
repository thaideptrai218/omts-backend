namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Who hears about what only the Cooperative Owner handles, such as a purchase request (BR-SALE-03).</summary>
public interface ICooperativeOwnerQuery
{
    /// <summary>The active Cooperative Owner accounts.</summary>
    Task<IReadOnlyList<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken);
}
