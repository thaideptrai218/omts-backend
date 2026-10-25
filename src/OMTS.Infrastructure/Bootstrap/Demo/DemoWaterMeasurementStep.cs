using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// ENV-01…ENV-04: the thresholds and the 30 days of water measurements of <see cref="DemoWaterPlans"/> for each In Use
/// demo zone, through the threshold, recording and correction services, so alerts, threshold snapshots, version chains
/// and audit rows come from the normal rules. A measurement is found by zone and device operation id (one per planned
/// day); its date is relative to the first seed day, which a later run reads back from the stored measurements of any
/// zone, so a rerun creates nothing and moves no date, and an interrupted run is completed on the same days.
/// </summary>
internal sealed class DemoWaterMeasurementStep : IDemoSeedStep
{
    public string Name => "water measurements";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var firstSeedDay = await FirstSeedDayAsync(context, cancellationToken);
        var created = 0;
        foreach (var zoneCode in DemoWaterPlans.Zones)
        {
            var zoneId = await context.Db.FarmingZones.Where(z => z.Code == zoneCode).Select(z => z.Id).SingleAsync(cancellationToken);
            await SetThresholdsOnceAsync(context, zoneId, zoneCode, cancellationToken);
            created += await RecordMissingDaysAsync(context, zoneId, zoneCode, firstSeedDay, cancellationToken);
            created += await CorrectAndVoidAsync(context, zoneId, zoneCode, cancellationToken);
        }

        return created;
    }

    /// <summary>The first seed day: read back from any stored demo reading of any zone (an interrupted run), else today.</summary>
    private static async Task<DateOnly> FirstSeedDayAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var daysByOperation = DemoWaterPlans.Zones
            .SelectMany(zone => Enumerable.Range(1, DemoWaterPlans.Days).Select(daysAgo => (Id: DemoWaterPlans.OperationId(zone, daysAgo), DaysAgo: daysAgo)))
            .ToDictionary(x => x.Id, x => x.DaysAgo);
        var operationIds = daysByOperation.Keys.ToList();
        var stored = await context.Db.WaterMeasurements.AsNoTracking()
            .Where(m => operationIds.Contains(m.ClientOperationId))
            .Select(m => new { m.ClientOperationId, m.MeasuredAt })
            .FirstOrDefaultAsync(cancellationToken);

        return stored is null ? context.Today : BusinessClock.ToBusinessDate(stored.MeasuredAt).AddDays(daysByOperation[stored.ClientOperationId]);
    }

    /// <summary>ENV-04, once per zone: any threshold change already audited (by an earlier run or by the Owner) is kept.</summary>
    private static async Task SetThresholdsOnceAsync(DemoSeedContext context, Guid zoneId, string zoneCode, CancellationToken cancellationToken)
    {
        if (await context.Db.AuditEvents.AnyAsync(e => e.Action == WaterMeasurementAudit.ThresholdUpdated && e.RecordId == zoneId, cancellationToken))
        {
            return;
        }

        DemoSeedContext.Ensure(await context.Get<WaterThresholdService>().SetAsync(zoneId, DemoWaterPlans.Thresholds, cancellationToken), $"thresholds of {zoneCode}");
    }

    private static async Task<int> RecordMissingDaysAsync(
        DemoSeedContext context, Guid zoneId, string zoneCode, DateOnly firstSeedDay, CancellationToken cancellationToken)
    {
        var daysByOperation = Enumerable.Range(1, DemoWaterPlans.Days).ToDictionary(d => DemoWaterPlans.OperationId(zoneCode, d));
        var operationIds = daysByOperation.Keys.ToList();
        var stored = await context.Db.WaterMeasurements.AsNoTracking()
            .Where(m => m.ZoneId == zoneId && operationIds.Contains(m.ClientOperationId))
            .Select(m => m.ClientOperationId)
            .ToListAsync(cancellationToken);

        var recording = context.Get<WaterMeasurementRecordingService>();
        var created = 0;
        foreach (var (operationId, daysAgo) in daysByOperation)
        {
            if (stored.Contains(operationId))
            {
                continue;
            }

            var reading = DemoWaterPlans.Reading(zoneCode, daysAgo);
            var day = firstSeedDay.AddDays(-daysAgo);
            var measuredAt = BusinessClock.FromLocal(day.ToDateTime(reading.Time));
            DemoSeedContext.Ensure(await recording.RecordAsync(
                zoneId, new RecordWaterMeasurementRequest(operationId, measuredAt, reading.Ph, reading.Salinity, Confirm: true), null, cancellationToken),
                $"water measurement of {zoneCode} on {day:yyyy-MM-dd}");
            created++;
        }

        return created;
    }

    /// <summary>ENV-02: the planned correction and void, each only while its reading is still the untouched Confirmed version.</summary>
    private static async Task<int> CorrectAndVoidAsync(DemoSeedContext context, Guid zoneId, string zoneCode, CancellationToken cancellationToken)
    {
        var corrections = context.Get<WaterMeasurementCorrectionService>();
        var created = 0;
        var correction = DemoWaterPlans.Correction;
        if (correction.Zone == zoneCode && await ConfirmedAsync(context, zoneId, zoneCode, correction.DaysAgo, cancellationToken) is { } typo)
        {
            DemoSeedContext.Ensure(await corrections.CorrectAsync(
                typo.Id, new CorrectWaterMeasurementRequest(typo.MeasuredAt, typo.Ph, correction.Salinity, correction.Reason, typo.RecordVersion), cancellationToken),
                $"correcting the water measurement of {zoneCode}");
            created++;
        }

        var voided = DemoWaterPlans.Void;
        if (voided.Zone == zoneCode && await ConfirmedAsync(context, zoneId, zoneCode, voided.DaysAgo, cancellationToken) is { } wrong)
        {
            DemoSeedContext.Ensure(
                await corrections.VoidAsync(wrong.Id, new VoidWaterMeasurementRequest(voided.Reason, wrong.RecordVersion), cancellationToken),
                $"voiding the water measurement of {zoneCode}");
        }

        return created;
    }

    // A corrected reading is Superseded and a voided one Voided: both are left as they are.
    private static Task<StoredReading?> ConfirmedAsync(DemoSeedContext context, Guid zoneId, string zoneCode, int daysAgo, CancellationToken cancellationToken)
    {
        var operationId = DemoWaterPlans.OperationId(zoneCode, daysAgo);
        return context.Db.WaterMeasurements.AsNoTracking()
            .Where(m => m.ZoneId == zoneId && m.ClientOperationId == operationId && m.StatusId == StatusIds.WaterMeasurement.Confirmed)
            .Select(m => new StoredReading(m.Id, m.MeasuredAt, m.Ph, m.RecordVersion))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed record StoredReading(Guid Id, DateTimeOffset MeasuredAt, decimal? Ph, int RecordVersion);
}
