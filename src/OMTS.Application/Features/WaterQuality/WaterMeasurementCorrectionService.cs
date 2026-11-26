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
/// WATER-02 – void a wrong measurement with a reason; a void is final and a measurement is never corrected (owner decision of
/// 26/11/2026). Who may do it: <see cref="WaterCorrectionPreconditions"/>; who is told: <see cref="WaterCorrectionNotifier"/>.
/// Past corrections stay in the audit log as <c>WaterMeasurement.Corrected</c> rows.
/// <para>
/// Water has no version check (NFR-09, NFR-13): nobody is told to reload and the last save counts. Parallel changes still
/// happen one at a time: a save that loses on the row version runs the whole change again from the record as it now is, so
/// an audit row starts where the one before it ended and a measurement voided meanwhile is not voided twice (MSG-GEN-09).
/// </para>
/// </summary>
public sealed class WaterMeasurementCorrectionService(
    WaterCorrectionPreconditions preconditions,
    WaterCorrectionNotifier notifier,
    WaterMeasurementViewBuilder views,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser)
{
    // Each lost round means another save of the record went through, so a request loses at most once per parallel request.
    private const int MaxSaveAttempts = 20;

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
                // Drops this attempt's audit and notification rows and its push and SMS messages too.
                unitOfWork.DiscardChanges();
            }
        }
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
