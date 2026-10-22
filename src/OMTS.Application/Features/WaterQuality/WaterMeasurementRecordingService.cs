using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// ENV-01 – record water measurements: photo → OCR draft → confirmation, or manual entry / offline sync in
/// one request. Re-sending a client operation returns the record already saved and never creates a second
/// record, alert or notification (NFR-15). Drafts are never evaluated against thresholds.
/// </summary>
public sealed class WaterMeasurementRecordingService(
    IWaterMeasurementRepository measurements,
    ZoneAccessGuard zoneAccess,
    IFileStorage storage,
    WaterMeterRecognizer recognizer,
    WaterAlertService alerts,
    WaterMeasurementViewBuilder views,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string PhotoCategory = "water-meter-photos";
    public const string PhotoField = "Photo";
    public const string ZoneField = "Zone";
    public const string ClientOperationField = "Client operation id";

    /// <summary>Stores the meter photo, reads it with OCR and saves a Draft; an OCR failure still saves the Draft.</summary>
    public async Task<Result<RecognitionResult>> RecognizeAsync(
        Guid zoneId, IncomingFile? photo, Guid? clientOperationId, CancellationToken cancellationToken)
    {
        var accountId = CallerId();
        var zone = await measurements.GetZoneAsync(zoneId, track: false, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        var operationId = clientOperationId is { } given && given != Guid.Empty ? given : Guid.NewGuid();
        var (previous, mismatch) = await FindPreviousAsync(accountId, operationId, zoneId, cancellationToken);
        if (mismatch is not null)
        {
            return mismatch;
        }

        if (previous is not null)
        {
            return new RecognitionResult(await views.DetailAsync(previous, previous.Zone, cancellationToken), OcrOutcome.NotCalled);
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Invalid(ZoneField);
        }

        var stored = await StorePhotoAsync(photo, required: true, cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error!;
        }

        var (file, key) = stored.Value!.Value;
        var outcome = await recognizer.RecognizeAsync(file.File.Content, file.ContentType, cancellationToken);
        var draft = New(zone, accountId, operationId);
        draft.PhotoUrl = key;
        WaterMeterRecognizer.Apply(draft, outcome);
        measurements.Add(draft);
        audit.Record(Entry(WaterMeasurementAudit.Created, draft, before: null));

        if (await SaveOrFindPreviousAsync(accountId, operationId, cancellationToken) is { } raced)
        {
            return new RecognitionResult(await views.DetailAsync(raced, raced.Zone, cancellationToken), OcrOutcome.NotCalled);
        }

        return new RecognitionResult(await views.DetailAsync(draft, zone, cancellationToken), outcome);
    }

    /// <summary>Sends the photo of a Draft that has no OCR values yet (kept offline "for recognition once online") to OCR.</summary>
    public async Task<Result<RecognitionResult>> RecognizeAgainAsync(Guid id, CancellationToken cancellationToken)
    {
        var draft = await measurements.GetAsync(id, cancellationToken);
        if (draft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.WaterMeasurements, PermissionAction.Create, draft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (draft.StatusId != StatusIds.WaterMeasurement.Draft)
        {
            return WaterMeasurementStatuses.CannotChange(statusCatalog, draft.StatusId, StatusIds.WaterMeasurement.Draft, "only a Draft can be sent to OCR again");
        }

        if (draft.PhotoUrl is not { } key || FileValidator.ContentTypeOfKey(key) is not { } contentType)
        {
            return AppError.Invalid(PhotoField);
        }

        if (draft.OcrPh is not null || draft.OcrSalinity is not null || draft.OcrMeasuredAt is not null)
        {
            return new RecognitionResult(await views.DetailAsync(draft, draft.Zone, cancellationToken), OcrOutcome.NotCalled);
        }

        await using var content = await storage.OpenReadAsync(key, cancellationToken);
        if (content is null)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.FileOperationFailed);
        }

        var outcome = await recognizer.RecognizeAsync(content, contentType, cancellationToken);
        if (outcome.Status == OcrStatus.Success)
        {
            WaterMeterRecognizer.Apply(draft, outcome);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new RecognitionResult(await views.DetailAsync(draft, draft.Zone, cancellationToken), outcome);
    }

    /// <summary>Manual entry and offline sync: <c>confirm=true</c> saves a Confirmed record (with the alert check), else a Draft.</summary>
    public async Task<Result<RecordedWaterMeasurement>> RecordAsync(
        Guid zoneId, RecordWaterMeasurementRequest request, IncomingFile? photo, CancellationToken cancellationToken)
    {
        var accountId = CallerId();
        var zone = await measurements.GetZoneAsync(zoneId, track: false, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (request.ClientOperationId is not { } operationId || operationId == Guid.Empty)
        {
            return AppError.Invalid(ClientOperationField);
        }

        var (previous, mismatch) = await FindPreviousAsync(accountId, operationId, zoneId, cancellationToken);
        if (mismatch is not null)
        {
            return mismatch;
        }

        if (previous is not null)
        {
            return new RecordedWaterMeasurement(await views.DetailAsync(previous, previous.Zone, cancellationToken), Replayed: true);
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Invalid(ZoneField);
        }

        var invalid = WaterValueRules.InvalidFields(request.Ph, request.Salinity, request.MeasuredAt, timeProvider.GetUtcNow(), required: request.Confirm);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var stored = await StorePhotoAsync(photo, required: false, cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error!;
        }

        var measurement = New(zone, accountId, operationId);
        measurement.MeasuredAt = request.MeasuredAt ?? measurement.MeasuredAt;
        measurement.Ph = request.Ph;
        measurement.Salinity = request.Salinity;
        measurement.PhotoUrl = stored.Value?.Key;
        measurements.Add(measurement);
        if (request.Confirm)
        {
            await alerts.ConfirmAsync(measurement, zone, WaterThresholds.Of(zone), accountId, cancellationToken);
        }

        audit.Record(Entry(request.Confirm ? WaterMeasurementAudit.Confirmed : WaterMeasurementAudit.Created, measurement, before: null));
        if (await SaveOrFindPreviousAsync(accountId, operationId, cancellationToken) is { } raced)
        {
            return new RecordedWaterMeasurement(await views.DetailAsync(raced, raced.Zone, cancellationToken), Replayed: true);
        }

        return new RecordedWaterMeasurement(await views.DetailAsync(measurement, zone, cancellationToken), Replayed: false);
    }

    /// <summary>Draft → Confirmed with the final values (omitted values keep the draft's), then the threshold check.</summary>
    public async Task<Result<WaterMeasurementDetail>> ConfirmAsync(Guid id, ConfirmWaterMeasurementRequest request, CancellationToken cancellationToken)
    {
        var measurement = await measurements.GetAsync(id, cancellationToken);
        if (measurement is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.WaterMeasurements, PermissionAction.Create, measurement.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (measurement.StatusId != StatusIds.WaterMeasurement.Draft)
        {
            return WaterMeasurementStatuses.CannotChange(
                statusCatalog, measurement.StatusId, StatusIds.WaterMeasurement.Confirmed, "only a Draft can be confirmed");
        }

        if (measurement.Zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Invalid(ZoneField);
        }

        var (ph, salinity, measuredAt) = (request.Ph ?? measurement.Ph, request.Salinity ?? measurement.Salinity, request.MeasuredAt ?? measurement.MeasuredAt);
        var invalid = WaterValueRules.InvalidFields(ph, salinity, measuredAt, timeProvider.GetUtcNow(), required: true);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var before = WaterMeasurementAudit.Snapshot(measurement, statusCatalog);
        (measurement.Ph, measurement.Salinity, measurement.MeasuredAt) = (ph, salinity, measuredAt);
        await alerts.ConfirmAsync(measurement, measurement.Zone, WaterThresholds.Of(measurement.Zone), CallerId(), cancellationToken);
        audit.Record(Entry(WaterMeasurementAudit.Confirmed, measurement, before));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(measurement, measurement.Zone, cancellationToken);
    }

    private WaterMeasurement New(FarmingZone zone, Guid accountId, Guid operationId) => new()
    {
        ZoneId = zone.Id,
        // Copied once; a later zone edit does not rewrite history.
        MeasurementPoint = zone.WaterMeasurementPoint,
        MeasuredAt = timeProvider.GetUtcNow(),
        InputSource = MeasurementInputSource.Manual,
        ClientOperationId = operationId,
        MeasuredByAccountId = accountId,
        StatusId = StatusIds.WaterMeasurement.Draft,
    };

    private AuditEntry Entry(string action, WaterMeasurement measurement, object? before) => new(action, WaterMeasurementAudit.RecordType)
    {
        RecordId = measurement.Id,
        ZoneId = measurement.ZoneId,
        Before = before,
        After = WaterMeasurementAudit.Snapshot(measurement, statusCatalog),
    };

    /// <summary>The checked photo and its storage key; nothing when an optional photo is absent.</summary>
    private async Task<Result<(ValidatedFile File, string Key)?>> StorePhotoAsync(IncomingFile? photo, bool required, CancellationToken cancellationToken)
    {
        if (photo is null)
        {
            return required ? AppError.Invalid(PhotoField) : Result<(ValidatedFile File, string Key)?>.Success(null);
        }

        var checkedPhoto = FileValidator.Validate([photo], FileRules.Photos);
        if (!checkedPhoto.IsValid)
        {
            return AppError.Invalid(PhotoField, checkedPhoto.Problem);
        }

        // Stored at original quality: it is the OCR evidence.
        var uploaded = await storage.UploadAsync(checkedPhoto.Files[0], PhotoCategory, cancellationToken);
        if (uploaded is not { Succeeded: true, Key: { } key })
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.FileOperationFailed);
        }

        return Result<(ValidatedFile, string)?>.Success((checkedPhoto.Files[0], key));
    }

    private async Task<(WaterMeasurement? Previous, AppError? Mismatch)> FindPreviousAsync(
        Guid accountId, Guid operationId, Guid zoneId, CancellationToken cancellationToken)
    {
        var previous = await measurements.FindByClientOperationAsync(accountId, operationId, cancellationToken);
        if (previous is null)
        {
            return (null, null);
        }

        // The same operation id for another zone is a client error, not a re-send.
        return previous.ZoneId == zoneId ? (previous, null) : (null, AppError.Invalid(ClientOperationField));
    }

    // A concurrent re-send passed the check above: the unique index keeps one record, the loser answers with it.
    private async Task<WaterMeasurement?> SaveOrFindPreviousAsync(Guid accountId, Guid operationId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            var previous = await measurements.FindByClientOperationAsync(accountId, operationId, cancellationToken);
            if (previous is null)
            {
                throw;
            }

            return previous;
        }
    }

    private Guid CallerId() => currentUser.AccountId ?? throw new InvalidOperationException("Water measurements need an authenticated caller.");
}
