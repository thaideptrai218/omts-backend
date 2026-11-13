using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>The history entries as answers: who recorded them, the materials with their stock issue, and signed links for the photos. The incident comes with its lane, photos and materials.</summary>
public sealed class LaneIncidentViews(IAccountRepository accounts, IFarmingInputItemRepository items, IInputIssueRepository issues, IFileStorage storage)
{
    public async Task<IReadOnlyList<LaneIncidentView>> ViewAsync(IReadOnlyList<LaneIncident> incidents, CancellationToken cancellationToken)
    {
        if (incidents.Count == 0)
        {
            return [];
        }

        var names = await accounts.GetDisplayNamesAsync(incidents.Select(i => i.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        var itemIds = incidents.SelectMany(i => i.Materials).Select(m => m.ItemId).Distinct().ToList();
        var itemsById = itemIds.Count == 0 ? [] : (await items.GetByIdsAsync(itemIds, cancellationToken)).ToDictionary(i => i.Id);
        var issueCodes = new Dictionary<Guid, string>();
        foreach (var issueId in incidents.SelectMany(i => i.Materials).Select(m => m.IssueId).OfType<Guid>().Distinct())
        {
            if (await issues.GetForEditAsync(issueId, cancellationToken) is { } issue)
            {
                issueCodes[issueId] = issue.Code;
            }
        }

        var views = new List<LaneIncidentView>(incidents.Count);
        foreach (var incident in incidents)
        {
            var photos = new List<LaneIncidentPhotoView>(incident.Photos.Count);
            foreach (var photo in incident.Photos.OrderBy(p => p.SortOrder))
            {
                photos.Add(new LaneIncidentPhotoView(photo.Id, photo.SortOrder, await storage.GetReadUrlAsync(photo.FileKey, cancellationToken: cancellationToken)));
            }

            var materials = incident.Materials
                .Where(m => itemsById.ContainsKey(m.ItemId))
                .Select(m => new LaneIncidentMaterialView(
                    m.ItemId, itemsById[m.ItemId].Code, itemsById[m.ItemId].Name, itemsById[m.ItemId].Unit, m.Quantity, m.IssueId,
                    m.IssueId is { } id && issueCodes.TryGetValue(id, out var code) ? code : null))
                .OrderBy(m => m.ItemCode, StringComparer.Ordinal)
                .ToList();
            views.Add(new LaneIncidentView(
                incident.Id, incident.LaneId, incident.Lane.LaneNumber, incident.Kind.ToString(), incident.OccurredAt, incident.OccurredOn, incident.CycleId, incident.ReasonCode,
                incident.Reason, incident.StateBefore, incident.HadSeed, incident.RecordedByAccountId, names.GetValueOrDefault(incident.RecordedByAccountId),
                incident.ResolvesIncidentId, photos, materials));
        }

        return views;
    }

    public async Task<LaneIncidentView> ViewAsync(LaneIncident incident, CancellationToken cancellationToken) => (await ViewAsync([incident], cancellationToken))[0];
}
