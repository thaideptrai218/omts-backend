using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The latest withdrawal of a lot as saved (TRACE-04 step 7 read again): its audit row and the sendings of its notices. Read only.</summary>
public interface IWithdrawalNoticeQuery
{
    /// <summary>The after value (JSON) of the latest <c>HarvestLot.Withdrawn</c> audit row of the lot; null when the lot was never withdrawn.</summary>
    Task<string?> LatestWithdrawalAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>The sendings of withdrawal notices of the lot with these ids (phone rows, never list rows), untracked.</summary>
    Task<IReadOnlyList<Notification>> SendingsAsync(Guid lotId, IReadOnlyCollection<Guid> sendingIds, CancellationToken cancellationToken);
}
