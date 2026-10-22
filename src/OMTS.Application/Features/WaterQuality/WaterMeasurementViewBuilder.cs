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

/// <summary>Builds the ENV-03 list rows and the detail (version chain, photo URL, current editors).</summary>
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
                m.Id, m.MeasuredAt, m.Ph, m.Salinity, m.MeasurementPoint, statusCatalog.DisplayNameOf(m.StatusId), statusCatalog.CodeOf(m.StatusId),
                m.InputSource, WaterMeasurementStatuses.AlertStateOf(m), m.ViolatedValues,
                Ref(m.MeasuredByAccountId, names), Ref(m.ConfirmedByAccountId, names), m.RecordVersion, m.PhotoUrl is not null))
            .ToList();
    }

    /// <summary>The detail as stored now (the chain is read from the database).</summary>
    public async Task<WaterMeasurementDetail> DetailAsync(WaterMeasurement m, FarmingZone zone, CancellationToken cancellationToken)
    {
        var chain = await measurements.GetVersionChainAsync(m.Id, cancellationToken);
        if (chain.Count == 0)
        {
            chain = [m];
        }

        var names = await NamesAsync(chain.Append(m).ToList(), cancellationToken);
        var editors = editing.EditorsOf(m.Id, currentUser.AccountId)
            .Select(e => new RecordEditorView(e.AccountId, e.Name, e.StartedAt))
            .ToList();
        var photoUrl = m.PhotoUrl is { } key ? await storage.GetReadUrlAsync(key, cancellationToken: cancellationToken) : null;
        var confirmedOnce = m.ConfirmedAt is not null;

        return new WaterMeasurementDetail(
            m.Id, zone.Id, zone.Code, zone.Name, m.MeasurementPoint, m.MeasuredAt, m.Ph, m.Salinity,
            statusCatalog.DisplayNameOf(m.StatusId), statusCatalog.CodeOf(m.StatusId), m.InputSource, photoUrl,
            new OcrDraftValues(m.OcrPh, m.OcrSalinity, m.OcrMeasuredAt),
            confirmedOnce ? WaterThresholds.UsedBy(m) : null,
            WaterMeasurementStatuses.AlertStateOf(m), m.ThresholdExceeded, m.ViolatedValues, m.AlertedAt,
            Ref(m.MeasuredByAccountId, names), Ref(m.ConfirmedByAccountId, names), m.ConfirmedAt,
            m.RecordVersion, m.CorrectedFromMeasurementId, m.CorrectionReason, m.VoidReason,
            chain[^1].Id == m.Id,
            chain.Select(v => Version(v, names)).ToList(),
            editors,
            Notice(editors));
    }

    /// <summary>MSG58 for the earliest other editor.</summary>
    public static UserMessage? Notice(IReadOnlyList<RecordEditorView> editors) =>
        editors.Count == 0 ? null : UserMessages.SomeoneIsEditing(editors[0].Name, BusinessClock.Format(editors[0].StartedAt));

    private WaterMeasurementVersion Version(WaterMeasurement v, IReadOnlyDictionary<Guid, string> names) => new(
        v.Id, v.RecordVersion, statusCatalog.DisplayNameOf(v.StatusId), statusCatalog.CodeOf(v.StatusId), v.MeasuredAt, v.Ph, v.Salinity,
        WaterMeasurementStatuses.AlertStateOf(v), v.ViolatedValues, v.CorrectionReason, v.VoidReason,
        Ref(v.ConfirmedByAccountId, names), v.ConfirmedAt, v.CreatedAt);

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IReadOnlyList<WaterMeasurement> items, CancellationToken cancellationToken)
    {
        var ids = items.SelectMany(m => new[] { m.MeasuredByAccountId, m.ConfirmedByAccountId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        return ids.Count == 0 ? new Dictionary<Guid, string>() : await measurements.GetAccountNamesAsync(ids, cancellationToken);
    }

    private static AccountRef? Ref(Guid? accountId, IReadOnlyDictionary<Guid, string> names) =>
        accountId is { } id ? new AccountRef(id, names.GetValueOrDefault(id, "Unknown account")) : null;
}

