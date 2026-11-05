using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// WATER-01…WATER-04: the thresholds and the 30 days of water measurements of <see cref="DemoWaterPlans"/> for each In
/// Use demo zone, through the threshold, recording and correction services, so photos, alerts, threshold copies and
/// audit rows come from the normal rules. A measurement is found by zone and client operation id (one per planned day);
/// its date is relative to the first seed day, which a later run reads back from the stored measurements of any zone, so
/// a rerun creates nothing and moves no date, and an interrupted run is completed on the same days.
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
            await CorrectAndVoidAsync(context, zoneId, zoneCode, cancellationToken);
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

    /// <summary>WATER-04, once per zone: any threshold change already audited (by an earlier run or by the Owner) is kept.</summary>
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
        var raftIds = await context.Db.Rafts.AsNoTracking().Where(r => r.ZoneId == zoneId).ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);

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
            var what = $"water measurement of {zoneCode} on {day:yyyy-MM-dd}";
            var photo = DemoSeedContext.Ensure(await recording.RecognizeAsync(zoneId, DemoFiles.MeterPhoto(), cancellationToken), what);
            DemoSeedContext.Ensure(
                await recording.RecordAsync(
                    zoneId,
                    new RecordWaterMeasurementRequest(raftIds[reading.RaftCode], measuredAt, reading.Ph, reading.Salinity, reading.Temperature, photo.PhotoKey, operationId),
                    cancellationToken),
                what);
            created++;
        }

        return created;
    }

    /// <summary>WATER-02: the planned correction and void, each only while its reading is still as it was recorded and Active.</summary>
    private static async Task CorrectAndVoidAsync(DemoSeedContext context, Guid zoneId, string zoneCode, CancellationToken cancellationToken)
    {
        var corrections = context.Get<WaterMeasurementCorrectionService>();
        var correction = DemoWaterPlans.Correction;
        if (correction.Zone == zoneCode
            && await ActiveAsync(context, zoneId, zoneCode, correction.DaysAgo, cancellationToken) is { } typo
            && typo.Salinity == correction.TypedSalinity)
        {
            DemoSeedContext.Ensure(
                await corrections.CorrectAsync(typo.Id, new CorrectWaterMeasurementRequest(typo.MeasuredAt, typo.Ph, correction.Salinity, typo.Temperature, correction.Reason), cancellationToken),
                $"correcting the water measurement of {zoneCode}");
        }

        var voided = DemoWaterPlans.Void;
        if (voided.Zone == zoneCode && await ActiveAsync(context, zoneId, zoneCode, voided.DaysAgo, cancellationToken) is { } wrong)
        {
            DemoSeedContext.Ensure(
                await corrections.VoidAsync(wrong.Id, new VoidWaterMeasurementRequest(voided.Reason), cancellationToken),
                $"voiding the water measurement of {zoneCode}");
        }
    }

    // A voided reading is left as it is.
    private static Task<StoredReading?> ActiveAsync(DemoSeedContext context, Guid zoneId, string zoneCode, int daysAgo, CancellationToken cancellationToken)
    {
        var operationId = DemoWaterPlans.OperationId(zoneCode, daysAgo);
        return context.Db.WaterMeasurements.AsNoTracking()
            .Where(m => m.ZoneId == zoneId && m.ClientOperationId == operationId && m.StatusId == StatusIds.WaterMeasurement.Active)
            .Select(m => new StoredReading(m.Id, m.MeasuredAt, m.Ph, m.Salinity, m.Temperature))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed record StoredReading(Guid Id, DateTimeOffset MeasuredAt, decimal Ph, decimal Salinity, decimal Temperature);
}
