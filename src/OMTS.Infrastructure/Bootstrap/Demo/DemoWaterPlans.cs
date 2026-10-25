using System.Security.Cryptography;
using System.Text;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>A demo reading taken <see cref="DaysAgo"/> days before the first seed day, early in the morning.</summary>
internal sealed record DemoWaterReading(int DaysAgo, TimeOnly Time, decimal Ph, decimal Salinity);

/// <summary>
/// ENV-01…ENV-04 demo data of the In Use demo zones: their thresholds and one reading a day for the 30 days before the
/// first seed day. Three readings are outside the thresholds (an algae bloom, heavy rain, a storm), one reading is
/// corrected (a salinity typed without its decimal point, itself an alert) and one is voided (meter not calibrated).
/// </summary>
internal static class DemoWaterPlans
{
    public const int Days = 30;

    public static readonly string[] Zones = [DemoFarmingZoneStep.ZoneOne, DemoFarmingZoneStep.ZoneTwo];

    /// <summary>Thresholds for oysters in Vân Đồn bay: pH 7.5–8.5, salinity 18–32 ‰.</summary>
    public static readonly SetWaterThresholdsRequest Thresholds = new(7.5m, 8.5m, 18m, 32m);

    /// <summary>The reading recorded with salinity 2.6 ‰ and corrected to 26.0 ‰ (ENV-02).</summary>
    public static readonly (string Zone, int DaysAgo, decimal TypedSalinity, decimal Salinity, string Reason) Correction =
        (DemoFarmingZoneStep.ZoneOne, 7, 2.6m, 26.0m, "Nhập nhầm dấu thập phân: độ mặn đúng trên máy đo là 26,0 ‰, không phải 2,6 ‰.");

    /// <summary>The reading voided afterwards (ENV-02): that day has no valid measurement.</summary>
    public static readonly (string Zone, int DaysAgo, string Reason) Void =
        (DemoFarmingZoneStep.ZoneTwo, 10, "Máy đo pH chưa được hiệu chuẩn lại, kết quả đo không dùng được.");

    // (pH, salinity) of the readings outside the thresholds, each raising its alert (MSG27).
    private static readonly Dictionary<(string Zone, int DaysAgo), (decimal Ph, decimal Salinity)> OutsideThresholds = new()
    {
        [(DemoFarmingZoneStep.ZoneOne, 12)] = (8.72m, 27.5m),
        [(DemoFarmingZoneStep.ZoneTwo, 18)] = (7.9m, 14.8m),
        [(DemoFarmingZoneStep.ZoneTwo, 4)] = (7.32m, 23.0m),
    };

    /// <summary>
    /// The reading of a zone <paramref name="daysAgo"/> days before the first seed day: around pH 8.0 and salinity
    /// 27 ‰ (DEMO-01) or 24.5 ‰ (DEMO-02), varying a little from day to day, always inside the thresholds unless planned.
    /// </summary>
    public static DemoWaterReading Reading(string zone, int daysAgo)
    {
        var (basePh, baseSalinity) = zone == DemoFarmingZoneStep.ZoneOne ? (8.05m, 27.0m) : (7.95m, 24.5m);
        var ph = basePh + 0.05m * (daysAgo * 7 % 9 - 4);
        var salinity = baseSalinity + 0.5m * (daysAgo * 5 % 7 - 3);
        if (OutsideThresholds.TryGetValue((zone, daysAgo), out var outside))
        {
            (ph, salinity) = outside;
        }
        else if ((zone, daysAgo) == (Correction.Zone, Correction.DaysAgo))
        {
            salinity = Correction.TypedSalinity;
        }

        return new DemoWaterReading(daysAgo, new TimeOnly(6, 30).AddMinutes(daysAgo % 4 * 10), ph, salinity);
    }

    /// <summary>
    /// The device operation id of a reading (ENV-01 idempotency key), the same on every run: the natural key of the demo
    /// measurement of that zone and day, so a rerun finds it instead of recording it again.
    /// </summary>
    public static Guid OperationId(string zone, int daysAgo) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"omts-demo-water/{zone}/{daysAgo}")).AsSpan(0, 16));
}
