using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// WATER-02 – correct or void a wrong measurement with a reason. A correction changes the values, the time and the raft
/// of the same record against its own thresholds; a void is final. Who may do it: <see cref="WaterCorrectionPreconditions"/>;
/// who is told: <see cref="WaterCorrectionNotifier"/>.
/// <para>
/// Water has no version check (NFR-09, NFR-13): nobody is told to reload and the last save counts. Parallel changes still
/// happen one at a time: a save that loses on the row version runs the whole change again from the record as it now is, so
/// the row always holds one request's values with their result, an audit row starts where the one before it ended, and a
/// correction never brings back or changes a measurement voided meanwhile (MSG-GEN-09).
/// </para>
/// </summary>
public sealed class WaterMeasurementCorrectionService(
    WaterCorrectionPreconditions preconditions,
    WaterCorrectionNotifier notifier,
    WaterOverdueRearm overdue,
    WaterMeasurementViewBuilder views,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    // Each lost round means another save of the record went through, so a request loses at most once per parallel request.
    private const int MaxSaveAttempts = 20;

    public Task<Result<WaterMeasurementDetail>> CorrectAsync(Guid id, CorrectWaterMeasurementRequest request, CancellationToken cancellationToken) =>
        SaveOneAtATimeAsync(() => ApplyCorrectionAsync(id, request, cancellationToken), cancellationToken);

    /// <summary>Active → Voided (final); excluded from charts, exports and the overdue check.</summary>
    public Task<Result<WaterMeasurementDetail>> VoidAsync(Guid id, VoidWaterMeasurementRequest request, CancellationToken cancellationToken) =>
        SaveOneAtATimeAsync(() => ApplyVoidAsync(id, request, cancellationToken), cancellationToken);

    private async Task<Result<WaterMeasurementDetail>> SaveOneAtATimeAsync(Func<Task<Result<WaterMeasurement>>> change, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var changed = await change();
            if (!changed.IsSuccess)
            {
                return changed.Error!;
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return await views.DetailAsync(changed.Value!, changed.Value!.Zone, cancellationToken);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxSaveAttempts)
            {
                // Drops this attempt's audit and notification rows too; its push messages went with the failed save.
                unitOfWork.DiscardChanges();
            }
        }
    }

    private async Task<Result<WaterMeasurement>> ApplyCorrectionAsync(Guid id, CorrectWaterMeasurementRequest request, CancellationToken cancellationToken)
    {
        var loaded = await preconditions.LoadChangeableAsync(id, CallerId(), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var measurement = loaded.Value!;
        var invalid = WaterValueRules.InvalidFields(request.Ph, request.Salinity, request.Temperature, request.MeasuredAt, timeProvider.GetUtcNow());
        var raft = await preconditions.RaftOfCorrectionAsync(measurement, request.RaftId, cancellationToken);
        if (raft is null)
        {
            invalid.Add(WaterMeasurementRecordingService.RaftField);
        }

        var reason = WaterValueRules.Reason(request.Reason);
        if (reason is null)
        {
            invalid.Add(WaterValueRules.ReasonField);
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var outsideBefore = WaterAlertService.Evaluate(measurement).ValuesOutside;
        var measuredAtBefore = measurement.MeasuredAt;
        // A correction that moves the answer to the last overdue reminder before the zone's other measurements re-arms it.
        await overdue.BeforeCorrectionAsync(measurement, request.MeasuredAt!.Value, cancellationToken);
        var before = WaterMeasurementAudit.Snapshot(measurement, statusCatalog);
        measurement.MeasuredAt = request.MeasuredAt!.Value;
        measurement.Ph = request.Ph!.Value;
        measurement.Salinity = request.Salinity!.Value;
        measurement.Temperature = request.Temperature!.Value;
        measurement.Raft = raft!;
        measurement.RaftId = raft!.Id;
        await notifier.AfterCorrectionAsync(measurement, outsideBefore, measuredAtBefore, reason!, CallerId(), cancellationToken);
        // Saved as changed even when the values stay the same, so the row version puts every correction in one order.
        unitOfWork.MarkChanged(measurement);
        AuditChange(WaterMeasurementAudit.Corrected, measurement, before, reason!);
        return measurement;
    }

    private async Task<Result<WaterMeasurement>> ApplyVoidAsync(Guid id, VoidWaterMeasurementRequest request, CancellationToken cancellationToken)
    {
        var loaded = await preconditions.LoadChangeableAsync(id, CallerId(), cancellationToken);
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

        // Voiding the measurement that answered the last overdue reminder re-arms it (WaterOverdueRule).
        await overdue.BeforeVoidAsync(measurement, cancellationToken);
        await notifier.AfterVoidAsync(measurement, reason, CallerId(), cancellationToken);
        var before = WaterMeasurementAudit.Snapshot(measurement, statusCatalog);
        measurement.StatusId = S.Voided;
        measurement.VoidReason = reason;
        AuditChange(WaterMeasurementAudit.Voided, measurement, before, reason);
        return measurement;
    }

    /// <summary>GBR-02: the values before and after with the reason, saved with the change.</summary>
    private void AuditChange(string action, WaterMeasurement measurement, object before, string reason) =>
        audit.Record(new AuditEntry(action, WaterMeasurementAudit.RecordType)
        {
            RecordId = measurement.Id,
            ZoneId = measurement.ZoneId,
            Before = before,
            After = WaterMeasurementAudit.Snapshot(measurement, statusCatalog),
            Reason = reason,
        });

    private Guid CallerId() => currentUser.AccountId ?? throw new InvalidOperationException("Water measurements need an authenticated caller.");
}
