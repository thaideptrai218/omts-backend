using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>The matrix of WATER-02.2 and "latest" of BR-WATER-04, without a database.</summary>
public sealed class WaterCorrectionNoticeRuleTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 1, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pH", "pH,Salinity", WaterCorrectionNotice.CorrectedAlert)]
    [InlineData("pH,Salinity", "pH", WaterCorrectionNotice.CorrectedAlert)]
    [InlineData("pH", "Salinity", WaterCorrectionNotice.CorrectedAlert)]
    [InlineData("", "Temperature", WaterCorrectionNotice.CorrectedAlert)]
    [InlineData("pH,Temperature", "", WaterCorrectionNotice.AlertWithdrawn)]
    [InlineData("pH,Salinity", "Salinity,pH", WaterCorrectionNotice.None)]
    [InlineData("pH", "pH", WaterCorrectionNotice.None)]
    [InlineData("", "", WaterCorrectionNotice.None)]
    public void A_correction_of_the_latest_follows_which_indicators_are_outside(string before, string after, WaterCorrectionNotice expected)
    {
        Assert.Equal(expected, WaterCorrectionNoticeRule.AfterCorrection(true, Set(before), Set(after)));
    }

    [Theory]
    [InlineData("pH", "pH,Salinity")]
    [InlineData("pH", "")]
    [InlineData("", "pH")]
    public void A_correction_of_an_older_one_sends_nothing(string before, string after)
    {
        Assert.Equal(WaterCorrectionNotice.None, WaterCorrectionNoticeRule.AfterCorrection(false, Set(before), Set(after)));
    }

    [Theory]
    [InlineData(true, true, WaterCorrectionNotice.AlertWithdrawn)]
    [InlineData(true, false, WaterCorrectionNotice.None)]
    [InlineData(false, true, WaterCorrectionNotice.None)]
    [InlineData(false, false, WaterCorrectionNotice.None)]
    public void A_void_withdraws_only_the_alert_of_the_latest(bool wasLatest, bool wasOutside, WaterCorrectionNotice expected)
    {
        Assert.Equal(expected, WaterCorrectionNoticeRule.AfterVoid(wasLatest, wasOutside));
    }

    [Fact]
    public void Alone_in_the_zone_is_the_latest_whatever_the_time()
    {
        var alone = Measurement(At, At);

        Assert.True(WaterCorrectionNoticeRule.IsLatest(alone, []));
        Assert.True(WaterCorrectionNoticeRule.IsLatest(alone, [Stored(alone, At.AddDays(9))]));
    }

    [Fact]
    public void The_corrected_time_counts_against_the_other_measurements_as_stored()
    {
        var other = Measurement(At, At);

        var movedAfter = Measurement(At.AddMinutes(1), At.AddDays(-1));
        var movedBefore = Measurement(At.AddMinutes(-1), At.AddDays(1));

        Assert.True(WaterCorrectionNoticeRule.IsLatest(movedAfter, [Stored(movedAfter, At.AddDays(-5)), other]));
        Assert.False(WaterCorrectionNoticeRule.IsLatest(movedBefore, [Stored(movedBefore, At.AddDays(5)), other]));
        Assert.False(WaterCorrectionNoticeRule.IsLatest(movedBefore, [other, Stored(movedBefore, At.AddDays(-5))]));
    }

    [Fact]
    public void At_the_same_time_the_one_saved_last_is_the_latest()
    {
        var other = Measurement(At, At);

        Assert.True(WaterCorrectionNoticeRule.IsLatest(Measurement(At, At.AddSeconds(1)), [other]));
        Assert.False(WaterCorrectionNoticeRule.IsLatest(Measurement(At, At.AddSeconds(-1)), [other]));
    }

    private static string[] Set(string names) => names.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static WaterMeasurement Measurement(DateTimeOffset measuredAt, DateTimeOffset savedAt) =>
        new() { MeasuredAt = measuredAt, CreatedAt = savedAt };

    /// <summary>The same measurement as the database still has it, before the correction is saved.</summary>
    private static WaterMeasurement Stored(WaterMeasurement measurement, DateTimeOffset measuredAt)
    {
        var stored = Measurement(measuredAt, measurement.CreatedAt);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(stored, measurement.Id);
        return stored;
    }
}
