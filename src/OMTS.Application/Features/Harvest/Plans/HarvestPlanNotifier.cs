using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// The notifications of a harvest plan, saved with the change and pushed after it commits. The Owner is not told of their own action (operational notice, PM decision OMTS-216), so an Owner who hands a plan
/// over in a zone with no Zone Manager, or who leaves lanes out, tells nobody but the other Owners.
/// </summary>
public sealed class HarvestPlanNotifier(IZoneRecipientQuery recipients, OwnerActorRecipientFilter ownerActorFilter, INotificationDispatcher notifications)
{
    private const string AssignedTitle = "Harvest plan assigned";
    private const string LanesSkippedTitle = "Harvest plan lanes left out";

    /// <summary>
    /// MSG-PLAN-05 to the Zone Managers of every one of <paramref name="zoneIds"/> (the zones of the plan, or the zones a change newly brought in), or to the Owners for a zone with none. Someone in several of the zones gets
    /// one notice naming them all, with the number of lines of those zones.
    /// </summary>
    public async Task AssignedAsync(HarvestPlan plan, IReadOnlyCollection<Guid> zoneIds, Guid actor, CancellationToken cancellationToken)
    {
        var zonesOf = new Dictionary<Guid, SortedSet<Guid>>();
        foreach (var zoneId in zoneIds.Distinct())
        {
            foreach (var recipient in await recipients.GetZoneManagersOrOwnersAsync(zoneId, cancellationToken))
            {
                if (!zonesOf.TryGetValue(recipient, out var zones))
                {
                    zonesOf[recipient] = zones = [];
                }

                zones.Add(zoneId);
            }
        }

        foreach (var group in zonesOf.GroupBy(r => string.Join(',', r.Value)))
        {
            var zones = group.First().Value;
            var told = await ownerActorFilter.WithoutOwnerActorAsync(group.Select(r => r.Key).ToList(), actor, cancellationToken);
            var lines = plan.Lines.Where(l => zones.Contains(l.Raft.ZoneId)).ToList();
            var codes = lines.Select(l => l.Raft.Zone.Code).Distinct().Order().ToList();
            await notifications.NotifyAsync(
                told,
                new NotificationContent(
                    NotificationTypes.HarvestPlanAssigned,
                    AssignedTitle,
                    UserMessages.PlanAssignedNotice(plan.Code, string.Join(", ", codes), plan.PlannedAt, lines.Count),
                    new RelatedRecord(RelatedRecordTypes.HarvestPlan, plan.Id, plan.Code)),
                cancellationToken);
        }
    }

    /// <summary>MSG-PLAN-06 to the Owners: lanes of a raft will not be harvested, with the reason.</summary>
    public async Task LanesSkippedAsync(HarvestPlan plan, HarvestPlanLine line, IReadOnlyList<int> laneNumbers, string reason, Guid actor, CancellationToken cancellationToken)
    {
        var told = await ownerActorFilter.WithoutOwnerActorAsync(await recipients.GetCooperativeOwnersAsync(cancellationToken), actor, cancellationToken);
        await notifications.NotifyAsync(
            told,
            new NotificationContent(
                NotificationTypes.HarvestPlanLanesSkipped,
                LanesSkippedTitle,
                UserMessages.PlanLanesSkippedNotice(plan.Code, line.Raft.Code, laneNumbers, reason),
                new RelatedRecord(RelatedRecordTypes.HarvestPlan, plan.Id, plan.Code)),
            cancellationToken);
    }
}
