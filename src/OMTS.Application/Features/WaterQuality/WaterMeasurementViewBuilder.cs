using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>Builds the WATER-03 list rows and the detail (photo URL, current editors). The measurements carry their raft.</summary>
public sealed class WaterMeasurementViewBuilder(
    IWaterMeasurementRepository measurements,
    IStatusCatalog statusCatalog,
    IFileStorage storage,
    EditingPresenceTracker editing,
    ICurrentUser currentUser)
{
    public async Task<IReadOnlyList<WaterMeasurementListItem>> ListItemsAsync(IReadOnlyList<WaterMeasurement> items, CancellationToken cancellationToken)
    {
        var names = await NamesAsync(items, cancellationToken);
        return items.Select(m => new WaterMeasurementListItem(
                m.Id, m.MeasuredAt, m.RaftId, m.Raft.Code, m.Ph, m.Salinity, m.Temperature,
                statusCatalog.DisplayNameOf(m.StatusId), statusCatalog.CodeOf(m.StatusId),
                WaterMeasurementStatuses.AlertStateOf(m), m.ViolatedValues, Ref(m.MeasuredByAccountId, names)))
            .ToList();
    }

    public async Task<WaterMeasurementDetail> DetailAsync(WaterMeasurement m, FarmingZone zone, CancellationToken cancellationToken)
    {
        var names = await NamesAsync([m], cancellationToken);
        var editors = editing.EditorsOf(m.Id, currentUser.AccountId)
            .Select(e => new RecordEditorView(e.AccountId, e.Name, e.StartedAt))
            .ToList();

        return new WaterMeasurementDetail(
            m.Id, zone.Id, zone.Code, zone.Name, m.RaftId, m.Raft.Code, m.MeasuredAt, m.Ph, m.Salinity, m.Temperature,
            statusCatalog.DisplayNameOf(m.StatusId), statusCatalog.CodeOf(m.StatusId),
            await storage.GetReadUrlAsync(m.PhotoUrl, cancellationToken: cancellationToken),
            WaterThresholds.UsedBy(m),
            WaterMeasurementStatuses.AlertStateOf(m), m.ThresholdExceeded, m.ViolatedValues, m.AlertedAt,
            Ref(m.MeasuredByAccountId, names), m.VoidReason,
            editors,
            Notice(editors));
    }

    /// <summary>MSG58 for the earliest other editor.</summary>
    public static UserMessage? Notice(IReadOnlyList<RecordEditorView> editors) =>
        editors.Count == 0 ? null : UserMessages.SomeoneIsEditing(editors[0].Name, BusinessClock.Format(editors[0].StartedAt));

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IReadOnlyList<WaterMeasurement> items, CancellationToken cancellationToken)
    {
        var ids = items.Select(m => m.MeasuredByAccountId).OfType<Guid>().Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<Guid, string>() : await measurements.GetAccountNamesAsync(ids, cancellationToken);
    }

    private static AccountRef? Ref(Guid? accountId, IReadOnlyDictionary<Guid, string> names) =>
        accountId is { } id ? new AccountRef(id, names.GetValueOrDefault(id, "Unknown account")) : null;
}
