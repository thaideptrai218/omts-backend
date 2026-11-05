using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// WATER-02 – correct or void a wrong measurement with a reason. A correction changes the values and the time of the
/// same record and is evaluated against the thresholds of the measurement; a void is final.
/// <para>
/// Interim until OMTS-219 W2: any holder of the Edit right in the zone may do it (own-only, zone In Use and the raft
/// come with W2), a correction alerts whenever a value is outside (the WATER-02.2 matrix and MSG-WATER-04 come with W2),
/// a void notifies nobody, and the editing indicator stays.
/// </para>
/// </summary>
public sealed class WaterMeasurementCorrectionService(
    IWaterMeasurementRepository measurements,
    ZoneAccessGuard zoneAccess,
    WaterAlertService alerts,
    WaterOverdueRearm overdue,
    WaterMeasurementViewBuilder views,
    EditingPresenceTracker editing,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<Result<WaterMeasurementDetail>> CorrectAsync(Guid id, CorrectWaterMeasurementRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForEditAsync(id, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var measurement = loaded.Value!;
        var invalid = WaterValueRules.InvalidFields(request.Ph, request.Salinity, request.Temperature, request.MeasuredAt, timeProvider.GetUtcNow());
        var reason = WaterValueRules.Reason(request.Reason);
        if (reason is null)
        {
            invalid.Add(WaterValueRules.ReasonField);
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (measurement.StatusId != S.Active)
        {
            return WaterMeasurementStatuses.CannotChange(statusCatalog, measurement.StatusId);
        }

        var accountId = CallerId();
        // A correction that moves the answer to the last overdue reminder before the zone's other measurements re-arms it.
        await overdue.BeforeCorrectionAsync(measurement, request.MeasuredAt!.Value, cancellationToken);
        var before = WaterMeasurementAudit.Snapshot(measurement, statusCatalog);
        measurement.MeasuredAt = request.MeasuredAt!.Value;
        measurement.Ph = request.Ph!.Value;
        measurement.Salinity = request.Salinity!.Value;
        measurement.Temperature = request.Temperature!.Value;
        // Against the thresholds of the measurement, never today's (BR-WATER-04).
        await alerts.EvaluateAsync(measurement, measurement.Zone.Name, measurement.Raft.Code, accountId, corrected: true, cancellationToken);
        audit.Record(new AuditEntry(WaterMeasurementAudit.Corrected, WaterMeasurementAudit.RecordType)
        {
            RecordId = measurement.Id,
            ZoneId = measurement.ZoneId,
            Before = before,
            After = WaterMeasurementAudit.Snapshot(measurement, statusCatalog),
            Reason = reason,
        });

        // A parallel save loses on the row version: 409 MSG06 from the exception handler.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        editing.Stop(measurement.Id, accountId);
        return await views.DetailAsync(measurement, measurement.Zone, cancellationToken);
    }

    /// <summary>Active → Voided (final); excluded from charts, exports and the overdue check.</summary>
    public async Task<Result<WaterMeasurementDetail>> VoidAsync(Guid id, VoidWaterMeasurementRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForEditAsync(id, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var measurement = loaded.Value!;
        var reason = WaterValueRules.Reason(request.Reason);
        if (reason is null)
        {
            return AppError.InvalidFields([WaterValueRules.ReasonField]);
        }

        if (measurement.StatusId != S.Active)
        {
            return WaterMeasurementStatuses.CannotChange(statusCatalog, measurement.StatusId);
        }

        // Voiding the measurement that answered the last overdue reminder re-arms it (WaterOverdueRule).
        await overdue.BeforeVoidAsync(measurement, cancellationToken);
        measurement.StatusId = S.Voided;
        measurement.VoidReason = reason;
        audit.Record(new AuditEntry(WaterMeasurementAudit.Voided, WaterMeasurementAudit.RecordType)
        {
            RecordId = measurement.Id,
            ZoneId = measurement.ZoneId,
            Before = new { Status = statusCatalog.CodeOf(S.Active) },
            After = new { Status = statusCatalog.CodeOf(S.Voided) },
            Reason = reason,
        });

        await unitOfWork.SaveChangesAsync(cancellationToken);
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

    private Guid CallerId() => currentUser.AccountId ?? throw new InvalidOperationException("Water measurements need an authenticated caller.");
}
