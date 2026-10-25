using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>
/// OMTS-214: how a correction chain re-arms the overdue reminder. A correction is confirmed "now", but only a
/// correction to a later measured day counts as a newer measurement; a same-day or earlier-day fix keeps the
/// reminder state of the version it corrects.
/// </summary>
public class WaterOverdueRearmRuleTests
{
    private static readonly TimeSpan Local = TimeSpan.FromHours(7);
    private static readonly DateOnly Today = new(2026, 10, 23);
    private static readonly DateTimeOffset Reminded = new(2026, 10, 1, 6, 0, 0, Local);

    [Fact]
    public void Uncorrected_measurement_is_known_when_it_was_confirmed()
    {
        var first = Version(new(2026, 8, 20, 8, 0, 0, Local), confirmedAt: new(2026, 8, 20, 8, 5, 0, Local));

        Assert.Equal(first.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([first]));
    }

    [Fact]
    public void Same_day_or_earlier_day_correction_after_the_reminder_keeps_the_reminder_of_the_corrected_version()
    {
        var first = Version(new(2026, 8, 20, 8, 0, 0, Local), confirmedAt: new(2026, 8, 20, 8, 5, 0, Local));
        var sameDay = Version(new(2026, 8, 20, 16, 30, 0, Local), confirmedAt: Reminded.AddDays(2));
        var earlierDay = Version(new(2026, 8, 19, 8, 0, 0, Local), confirmedAt: Reminded.AddDays(3));

        Assert.Equal(first.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([first, sameDay]));
        Assert.Equal(first.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([first, sameDay, earlierDay]));
        Assert.False(WaterOverdueRule.NeedsReminder(sameDay.MeasuredAt, Reminded, Today, WaterOverdueRule.PeriodKnownAt([first, sameDay])));
    }

    [Fact]
    public void Correction_to_a_later_day_counts_as_a_newer_measurement_confirmed_at_the_correction()
    {
        var typo = Version(new(2026, 8, 20, 8, 0, 0, Local), confirmedAt: new(2026, 9, 20, 8, 5, 0, Local));
        var realDay = Version(new(2026, 9, 20, 8, 0, 0, Local), confirmedAt: Reminded.AddDays(2));
        var timeFix = Version(new(2026, 9, 20, 9, 0, 0, Local), confirmedAt: Reminded.AddDays(5));

        Assert.Equal(realDay.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([typo, realDay]));
        Assert.Equal(realDay.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([typo, realDay, timeFix]));
        // On time until 20/10, overdue (and reminded again) from 21/10 although the reminder of 01/10 came after it was measured.
        Assert.False(WaterOverdueRule.NeedsReminder(realDay.MeasuredAt, Reminded, new DateOnly(2026, 10, 20), realDay.ConfirmedAt));
        Assert.True(WaterOverdueRule.NeedsReminder(realDay.MeasuredAt, Reminded, Today, realDay.ConfirmedAt));
    }

    // Lead decision A: compared with every earlier version (the earliest one on or after its day), not only the one it corrects.
    [Fact]
    public void Each_correction_is_compared_with_all_earlier_versions_of_its_chain()
    {
        var first = Version(new(2026, 8, 20, 8, 0, 0, Local), confirmedAt: new(2026, 8, 20, 8, 5, 0, Local));
        var earlier = Version(new(2026, 8, 10, 8, 0, 0, Local), confirmedAt: new(2026, 8, 21, 8, 0, 0, Local));
        var laterThanEarlier = Version(new(2026, 8, 15, 8, 0, 0, Local), confirmedAt: new(2026, 8, 22, 8, 0, 0, Local));
        var later = Version(new(2026, 8, 30, 8, 0, 0, Local), confirmedAt: new(2026, 8, 30, 8, 5, 0, Local));
        var backBetween = Version(new(2026, 8, 25, 8, 0, 0, Local), confirmedAt: new(2026, 8, 31, 8, 0, 0, Local));

        // 08-20 → 08-10 → 08-15: still before 08-20, the same measurement as the first version.
        Assert.Equal(first.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([first, earlier, laterThanEarlier]));
        // 08-20 → 08-30 → 08-25: before 08-30 but after 08-20, known since the 08-30 version.
        Assert.Equal(later.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([first, later, backBetween]));
    }

    // The job reads the latest version and then its chain: a correction saved in between is ignored.
    [Fact]
    public void Versions_after_the_one_read_are_ignored()
    {
        var first = Version(new(2026, 8, 20, 8, 0, 0, Local), confirmedAt: new(2026, 8, 20, 8, 5, 0, Local));
        var savedLater = Version(new(2026, 9, 20, 8, 0, 0, Local), confirmedAt: Reminded.AddDays(1));

        Assert.Equal(first.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([first, savedLater], upTo: first.Id));
        Assert.Equal(savedLater.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([first, savedLater]));
        Assert.Null(WaterOverdueRule.PeriodKnownAt([]));
    }

    [Fact]
    public void Day_is_the_business_date_not_the_utc_date()
    {
        // 19/08 18:00 UTC is 20/08 01:00 local: the same day as the corrected 20/08 08:00.
        var first = Version(new(2026, 8, 20, 8, 0, 0, Local), confirmedAt: new(2026, 8, 20, 8, 5, 0, Local));
        var sameLocalDay = Version(new(2026, 8, 19, 18, 0, 0, TimeSpan.Zero), confirmedAt: Reminded.AddDays(1));

        Assert.Equal(first.ConfirmedAt, WaterOverdueRule.PeriodKnownAt([first, sameLocalDay]));
    }

    private static WaterMeasurement Version(DateTimeOffset measuredAt, DateTimeOffset confirmedAt) =>
        new() { MeasuredAt = measuredAt, ConfirmedAt = confirmedAt };
}
