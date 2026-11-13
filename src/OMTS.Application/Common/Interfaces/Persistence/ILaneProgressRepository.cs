using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The manual changes of the progress of the lanes of a cycle (decision of 12/11/2026): the corrections the lane percentages are worked out from, and the history shown.</summary>
public interface ILaneProgressRepository
{
    /// <summary>Every change of every lane of the cycle, read only, the oldest first.</summary>
    Task<IReadOnlyList<LaneProgressStep>> StepsAsync(Guid cycleId, CancellationToken cancellationToken);

    void Add(LaneProgressChange change);
}
