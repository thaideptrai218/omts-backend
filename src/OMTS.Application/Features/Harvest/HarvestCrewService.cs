using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Harvest;

/// <summary>The employees assigned to the raft on a day (LABOR-02), offered as the crew of a harvest there; any other employee can still be chosen.</summary>
public sealed record HarvestCrewSuggestions(Guid RaftId, string RaftCode, DateOnly Date, IReadOnlyList<CrewMemberView> Employees);

/// <summary>
/// The suggestion for the crew of a harvest lot (decision of 12/11/2026): the employees assigned to the raft that day. The rules of the crew itself are the same for the nursery, seeding and harvest
/// records and are in <see cref="RecordCrewService"/>.
/// </summary>
public sealed class HarvestCrewService(IWorkAssignmentRepository assignments, IRaftRepository rafts, ZoneAccessGuard zoneAccess, TimeProvider timeProvider)
{
    /// <summary>
    /// The employees assigned to the raft on the day (default today): who the Zone Manager would put on the crew first. Read only, for those who may view harvests of the raft's zone.
    /// </summary>
    public async Task<Result<HarvestCrewSuggestions>> SuggestAsync(Guid raftId, DateOnly? date, CancellationToken cancellationToken)
    {
        var raft = (await rafts.GetByIdsAsync([raftId], cancellationToken)).FirstOrDefault();
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.View, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var day = date ?? timeProvider.Today();
        var onRaft = (await assignments.ListDayAsync(day, cancellationToken))
            .Where(a => a.RaftId == raftId && a.Employee.StatusId == StatusIds.Employee.Working)
            .Select(a => a.Employee)
            .DistinctBy(e => e.Id)
            .Select(e => new CrewMemberView(e.Id, e.Code, e.FullName))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new HarvestCrewSuggestions(raft.Id, raft.Code, day, onRaft);
    }
}
