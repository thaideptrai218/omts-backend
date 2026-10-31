using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// ENV-02 – correct or void a wrong measurement with a reason. A correction is a new Confirmed version
/// evaluated against the thresholds the corrected one used; the old version becomes Corrected and is never
/// changed otherwise. Saving an outdated <c>recordVersion</c> is refused with the newest version (MSG06);
/// the editing indicator (MSG58) only warns, it never locks.
/// </summary>
public sealed class WaterMeasurementCorrectionService(
    IWaterMeasurementRepository measurements,
    ZoneAccessGuard zoneAccess,
    WaterAlertService alerts,
    WaterOverdueRearm overdue,
    WaterMeasurementViewBuilder views,
    EditingPresenceTracker editing,
    RowVersionGuard rowVersions,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<Result<WaterMeasurementDetail>> CorrectAsync(Guid id, CorrectWaterMeasurementRequest request, CancellationToken cancellationToken)
    {
        var old = await LoadForEditAsync(id, cancellationToken);
        if (!old.IsSuccess)
        {
            return old.Error!;
        }

        var current = old.Value!;
        var invalid = WaterValueRules.InvalidFields(request.Ph, request.Salinity, request.MeasuredAt, timeProvider.GetUtcNow(), required: true);
        var reason = WaterValueRules.Reason(request.Reason);
        invalid.AddRange(Missing(reason, request.RecordVersion));
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await CheckVersionAsync(current, request.RecordVersion!.Value, S.Superseded, cancellationToken) is { } refused)
        {
            return refused;
        }

        // A correction that moves the answer to the last overdue reminder before the zone's other measurements re-arms it.
        await overdue.BeforeCorrectionAsync(current, request.MeasuredAt!.Value, cancellationToken);
        var accountId = CallerId();
        var next = new WaterMeasurement
        {
            ZoneId = current.ZoneId,
            MeasurementPoint = current.MeasurementPoint,
            MeasuredAt = request.MeasuredAt!.Value,
            Ph = request.Ph,
            Salinity = request.Salinity,
            InputSource = MeasurementInputSource.Manual,
            // The evidence is kept: same photo and first OCR draft.
            PhotoUrl = current.PhotoUrl,
            OcrPh = current.OcrPh,
            OcrSalinity = current.OcrSalinity,
            OcrMeasuredAt = current.OcrMeasuredAt,
            MeasuredByAccountId = current.MeasuredByAccountId,
            ClientOperationId = Guid.NewGuid(),
            RecordVersion = current.RecordVersion + 1,
            CorrectedFromMeasurementId = current.Id,
            CorrectionReason = reason,
        };
        var before = WaterMeasurementAudit.Snapshot(current, statusCatalog);
        measurements.Add(next);
        current.StatusId = S.Superseded;
        // Re-evaluated against the thresholds the corrected measurement used, not today's.
        await alerts.ConfirmAsync(next, current.Zone, WaterThresholds.UsedBy(current), accountId, cancellationToken);
        audit.Record(new AuditEntry(WaterMeasurementAudit.Corrected, WaterMeasurementAudit.RecordType)
        {
            RecordId = current.Id,
            ZoneId = current.ZoneId,
            Before = before,
            After = new { NewMeasurementId = next.Id, Corrected = WaterMeasurementAudit.Snapshot(current, statusCatalog), New = WaterMeasurementAudit.Snapshot(next, statusCatalog) },
            Reason = reason,
        });

        if (await SaveOrConflictAsync(current, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        editing.Stop(current.Id, accountId);
        return await views.DetailAsync(next, current.Zone, cancellationToken);
    }

    /// <summary>Draft or the latest Confirmed version → Voided; excluded from charts, reports, exports and the overdue check.</summary>
    public async Task<Result<WaterMeasurementDetail>> VoidAsync(Guid id, VoidWaterMeasurementRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForEditAsync(id, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var measurement = loaded.Value!;
        var reason = WaterValueRules.Reason(request.Reason);
        var invalid = Missing(reason, request.RecordVersion);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await CheckVersionAsync(measurement, request.RecordVersion!.Value, S.Voided, cancellationToken) is { } refused)
        {
            return refused;
        }

        var fromStatus = measurement.StatusId;
        // Voiding the measurement that answered the last overdue reminder re-arms it (WaterOverdueRule).
        await overdue.BeforeVoidAsync(measurement, cancellationToken);
        measurement.StatusId = S.Voided;
        measurement.VoidReason = reason;
        audit.Record(new AuditEntry(WaterMeasurementAudit.Voided, WaterMeasurementAudit.RecordType)
        {
            RecordId = measurement.Id,
            ZoneId = measurement.ZoneId,
            Before = new { Status = statusCatalog.CodeOf(fromStatus) },
            After = new { Status = statusCatalog.CodeOf(S.Voided) },
            Reason = reason,
        });

        if (await SaveOrConflictAsync(measurement, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        editing.Stop(measurement.Id, CallerId());
        return await views.DetailAsync(measurement, measurement.Zone, cancellationToken);
    }

    /// <summary>Starts or keeps alive the caller's edit of the record (heartbeat); returns who else is editing (MSG58).</summary>
    public async Task<Result<EditingStatus>> StartEditingAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadForEditAsync(id, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var accountId = CallerId();
        var names = await measurements.GetAccountNamesAsync([accountId], cancellationToken);
        editing.Heartbeat(id, accountId, names.GetValueOrDefault(accountId, "Unknown account"));
        var others = editing.EditorsOf(id, accountId).Select(e => new RecordEditorView(e.AccountId, e.Name, e.StartedAt)).ToList();
        return new EditingStatus(others, WaterMeasurementViewBuilder.Notice(others));
    }

    public async Task<Result<Done>> StopEditingAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadForEditAsync(id, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        editing.Stop(id, CallerId());
        return Done.Value;
    }

    private async Task<Result<WaterMeasurement>> LoadForEditAsync(Guid id, CancellationToken cancellationToken)
    {
        var measurement = await measurements.GetAsync(id, cancellationToken);
        if (measurement is null)
        {
            return AppError.NotFound;
        }

        return await zoneAccess.AllowsAsync(PermissionFunctions.WaterMeasurements, PermissionAction.Edit, measurement.ZoneId, cancellationToken)
            ? measurement
            : AppError.Forbidden;
    }

    /// <summary>
    /// MSG06 when the chain has a newer version than the one edited; MSG-GEN-09 when the record's status does
    /// not allow the change (only a confirmed measurement can be corrected, a Draft can only be voided, Corrected
    /// and Voided are final).
    /// </summary>
    private async Task<AppError?> CheckVersionAsync(WaterMeasurement measurement, int recordVersion, int target, CancellationToken cancellationToken)
    {
        var chain = await measurements.GetVersionChainAsync(measurement.Id, cancellationToken);
        if (chain.Count > 0 && chain[^1].RecordVersion > recordVersion)
        {
            return await NewerVersionAsync(chain[^1], measurement.Zone, cancellationToken);
        }

        if ((measurement.StatusId, target) is not ((S.Confirmed, _) or (S.Draft, S.Voided)))
        {
            return WaterMeasurementStatuses.CannotChange(statusCatalog, measurement.StatusId);
        }

        return recordVersion == measurement.RecordVersion ? null : AppError.Invalid(WaterValueRules.RecordVersionField);
    }

    /// <summary>409 MSG06 naming who saved the newest version and when, with that version for the client to show.</summary>
    private async Task<AppError> NewerVersionAsync(WaterMeasurement latest, FarmingZone zone, CancellationToken cancellationToken)
    {
        var conflict = await rowVersions.ConflictAsync(latest, cancellationToken);
        var details = new Dictionary<string, object?>(conflict.Details ?? new Dictionary<string, object?>())
        {
            ["latestVersion"] = await views.DetailAsync(latest, zone, cancellationToken),
        };
        return conflict with { Details = details };
    }

    // Two saves raced past the version check: the row version (and the unique chain index) let one win.
    private async Task<AppError?> SaveOrConflictAsync(WaterMeasurement edited, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is ConcurrencyConflictException or DuplicateRecordException)
        {
            var chain = await measurements.GetVersionChainAsync(edited.Id, cancellationToken);
            return await NewerVersionAsync(chain[^1], edited.Zone, cancellationToken);
        }
    }

    private static List<string> Missing(string? reason, int? recordVersion)
    {
        var invalid = new List<string>();
        if (reason is null)
        {
            invalid.Add(WaterValueRules.ReasonField);
        }

        if (recordVersion is not > 0)
        {
            invalid.Add(WaterValueRules.RecordVersionField);
        }

        return invalid;
    }

    private Guid CallerId() => currentUser.AccountId ?? throw new InvalidOperationException("Water measurements need an authenticated caller.");
}
