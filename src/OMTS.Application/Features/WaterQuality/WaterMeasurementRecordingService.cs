using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// WATER-01 – record a water measurement in two steps: recognize stores the meter photo and answers the OCR suggestions
/// without saving a measurement; the record request saves the values the user confirmed as Active with a copy of the
/// zone thresholds, and alerts when a value is outside them. Nothing exists before that confirmation (AC-04). Re-sending
/// a client operation returns the record already saved and never creates a second record, alert or notification.
/// </summary>
public sealed class WaterMeasurementRecordingService(
    IWaterMeasurementRepository measurements,
    WaterMeterPhotoStore photos,
    WaterMeterRecognizer recognizer,
    WaterAlertService alerts,
    WaterMeasurementViewBuilder views,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string PhotoField = "Photo";
    public const string ZoneField = "Zone";
    public const string RaftField = "Raft";
    public const string ClientOperationField = "Client operation id";

    /// <summary>Stores the photo and reads it (steps 5–6). An OCR failure still answers the key: the user types the values in.</summary>
    public async Task<Result<RecognitionResult>> RecognizeAsync(Guid zoneId, IncomingFile? photo, CancellationToken cancellationToken)
    {
        var zone = await measurements.GetZoneAsync(zoneId, track: false, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Invalid(ZoneField);
        }

        if (!await measurements.HasRaftInUseAsync(zoneId, cancellationToken))
        {
            return AppError.Invalid(RaftField);
        }

        var stored = await photos.StoreAsync(photo, CallerId(), cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error!;
        }

        var (file, key) = stored.Value;
        var outcome = await recognizer.RecognizeAsync(file.File.Content, file.ContentType, cancellationToken);
        var outside = WaterThresholdEvaluator.Evaluate(outcome.Ph?.Value, outcome.Salinity?.Value, outcome.Temperature?.Value, WaterThresholds.Of(zone));
        return new RecognitionResult(
            key,
            timeProvider.GetUtcNow(),
            outcome,
            outside.ValuesOutside,
            outside.Exceeded ? UserMessages.WaterValuesOutsideBeforeSave(outside.ValuesOutside) : null);
    }

    /// <summary>Saves the confirmed values (steps 8–10).</summary>
    public async Task<Result<RecordedWaterMeasurement>> RecordAsync(Guid zoneId, RecordWaterMeasurementRequest request, CancellationToken cancellationToken)
    {
        var accountId = CallerId();
        var zone = await measurements.GetZoneAsync(zoneId, track: false, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        var operationId = request.ClientOperationId is { } given && given != Guid.Empty ? given : Guid.NewGuid();
        var previous = await measurements.FindByClientOperationAsync(accountId, operationId, cancellationToken);
        if (previous is not null)
        {
            // The same operation id for another zone is a client error, not a re-send.
            return previous.ZoneId == zoneId
                ? new RecordedWaterMeasurement(await views.DetailAsync(previous, previous.Zone, cancellationToken), Replayed: true)
                : AppError.Invalid(ClientOperationField);
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Invalid(ZoneField);
        }

        var invalid = WaterValueRules.InvalidFields(request.Ph, request.Salinity, request.Temperature, request.MeasuredAt, timeProvider.GetUtcNow());
        var raft = request.RaftId is { } raftId ? await measurements.GetRaftAsync(raftId, cancellationToken) : null;
        if (raft is null || raft.ZoneId != zoneId || raft.StatusId != StatusIds.Usage.InUse)
        {
            invalid.Add(RaftField);
        }

        if (!await photos.IsUsableAsync(request.PhotoKey, accountId, cancellationToken))
        {
            invalid.Add(PhotoField);
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var measurement = new WaterMeasurement
        {
            ZoneId = zoneId,
            RaftId = raft!.Id,
            MeasuredAt = request.MeasuredAt!.Value,
            Ph = request.Ph!.Value,
            Salinity = request.Salinity!.Value,
            Temperature = request.Temperature!.Value,
            PhotoUrl = request.PhotoKey!,
            ClientOperationId = operationId,
            MeasuredByAccountId = accountId,
        };
        WaterThresholds.Of(zone).CopyTo(measurement);
        measurements.Add(measurement);
        await alerts.EvaluateAsync(measurement, zone.Name, raft.Code, accountId, corrected: false, cancellationToken);
        audit.Record(new AuditEntry(WaterMeasurementAudit.Created, WaterMeasurementAudit.RecordType)
        {
            RecordId = measurement.Id,
            ZoneId = zoneId,
            After = WaterMeasurementAudit.Snapshot(measurement, statusCatalog),
        });

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A concurrent re-send or a second use of the photo passed the checks above: the unique indexes keep one
            // record, and the loser answers with it, or with the refused photo.
            var raced = await measurements.FindByClientOperationAsync(accountId, operationId, cancellationToken);
            return raced is null
                ? AppError.Invalid(PhotoField)
                : new RecordedWaterMeasurement(await views.DetailAsync(raced, raced.Zone, cancellationToken), Replayed: true);
        }

        return new RecordedWaterMeasurement(await views.DetailAsync(measurement, zone, cancellationToken), Replayed: false);
    }

    private Guid CallerId() => currentUser.AccountId ?? throw new InvalidOperationException("Water measurements need an authenticated caller.");
}
