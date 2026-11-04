using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>PR 0.8 pure rules: the GFR-03 notice due per state, the newest license of a zone and its badge, the GBR-03 code proposal.</summary>
public class ExpiryReminderAndZoneCodeRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 31);
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 1, 0, 0, TimeSpan.Zero);
    private static readonly Guid Zone = Guid.NewGuid();

    [Fact]
    public void Each_state_gets_its_notice_once_and_a_record_found_expired_gets_only_the_expired_one()
    {
        var record = new Markers();
        Assert.Null(ExpiryReminderRule.Due(expiringSoon: false, expired: false, record));
        Assert.Equal(ExpiryNotice.ExpiringSoon, ExpiryReminderRule.Due(expiringSoon: true, expired: false, record));

        ExpiryReminderRule.MarkSent(record, ExpiryNotice.ExpiringSoon, Now);
        Assert.Null(ExpiryReminderRule.Due(expiringSoon: true, expired: false, record));
        Assert.Equal(ExpiryNotice.Expired, ExpiryReminderRule.Due(expiringSoon: false, expired: true, record));
        ExpiryReminderRule.MarkSent(record, ExpiryNotice.Expired, Now.AddDays(10));
        Assert.Null(ExpiryReminderRule.Due(expiringSoon: false, expired: true, record));
        Assert.Equal((Now, Now.AddDays(10)), (record.ExpiringReminderSentAt, record.ExpiredReminderSentAt));

        var foundExpired = new Markers();
        ExpiryReminderRule.MarkSent(foundExpired, ExpiryReminderRule.Due(expiringSoon: false, expired: true, foundExpired)!.Value, Now);
        Assert.Equal((Now, Now), (foundExpired.ExpiringReminderSentAt, foundExpired.ExpiredReminderSentAt));
        Assert.Null(ExpiryReminderRule.Due(expiringSoon: true, expired: false, foundExpired));
    }

    [Fact]
    public void Only_a_new_expiry_date_re_arms_the_markers()
    {
        var record = new Markers { ExpiryDate = Today, ExpiringReminderSentAt = Now, ExpiredReminderSentAt = Now };

        ExpiryReminderRule.RearmOnNewExpiry(record, Today);
        Assert.Equal((Now, Now), (record.ExpiringReminderSentAt, record.ExpiredReminderSentAt));

        ExpiryReminderRule.RearmOnNewExpiry(record, Today.AddDays(1));
        Assert.Equal((null, null), (record.ExpiringReminderSentAt, record.ExpiredReminderSentAt));
    }

    // The newest license of a zone (zone badge and overview): the latest effective date that is not Replaced, then the latest recorded.
    [Fact]
    public void Newest_license_of_a_zone_skips_replaced_ones_and_breaks_ties_by_record_time()
    {
        var old = License(Today.AddYears(-3), Today.AddDays(-400), recorded: Now.AddDays(-900));
        var current = License(Today.AddYears(-1), Today.AddDays(20), recorded: Now.AddDays(-300));
        var sameDayLater = License(Today.AddYears(-1), Today.AddDays(400), recorded: Now.AddDays(-200));
        var renewalReplaced = License(Today.AddDays(5), Today.AddYears(5), recorded: Now, replacedAt: Now);
        var otherZone = License(Today.AddDays(1), Today.AddYears(2), recorded: Now, zone: Guid.NewGuid());
        var all = new[] { old, current, sameDayLater, renewalReplaced, otherZone };

        Assert.Same(sameDayLater, ZoneLicenseOverview.Newest(Zone, all));
        Assert.Same(current, ZoneLicenseOverview.Newest(Zone, [old, current]));
        Assert.Null(ZoneLicenseOverview.Newest(Guid.NewGuid(), all));
        Assert.Equal([renewalReplaced, sameDayLater, current, old], ZoneLicenseOverview.For(Zone, all, Today).Select(r => r.License));
        Assert.Equal(LicenseState.Replaced, ZoneLicenseOverview.For(Zone, all, Today)[0].State);
    }

    [Fact]
    public void Zone_badge_is_the_expired_or_expiring_state_of_the_newest_license_only()
    {
        var expiring = License(Today.AddYears(-1), Today.AddDays(30), recorded: Now);
        var expired = License(Today.AddYears(-1), Today.AddDays(-1), recorded: Now);
        var olderExpired = License(Today.AddYears(-5), Today.AddYears(-4), recorded: Now);
        var valid = License(Today.AddYears(-1), Today.AddDays(31), recorded: Now);

        Assert.Equal(LicenseState.ExpiringSoon, ZoneLicenseOverview.BadgeFor(Zone, [expiring, olderExpired], Today));
        Assert.Equal(LicenseState.Expired, ZoneLicenseOverview.BadgeFor(Zone, [expired], Today));
        Assert.Null(ZoneLicenseOverview.BadgeFor(Zone, [valid, olderExpired], Today));
        Assert.Null(ZoneLicenseOverview.BadgeFor(Zone, [], Today));
    }

    // GBR-03: KV01, KV02…; the highest number of the pattern plus one, other codes ignored, at least two digits.
    [Theory]
    [InlineData(new string[0], "KV01")]
    [InlineData(new[] { "KV01", "KV02" }, "KV03")]
    [InlineData(new[] { "kv09", "KV-NORTH", "ZONE12", "KVA1", " KV3 " }, "KV10")]
    [InlineData(new[] { "KV99" }, "KV100")]
    [InlineData(new[] { "KV99999999999999999999" }, "KV100000000000000000000")]
    public void Zone_code_proposal_follows_the_kv_pattern(string[] existing, string expected) =>
        Assert.Equal(expected, SequentialCodes.Next(SequentialCodes.ZonePrefix, SequentialCodes.ZoneDigits, SequentialCodes.ZoneCodeMaxLength, existing));

    // A proposal can always be saved: it fits the Code field and is free, even next to a used number that has no next code.
    [Fact]
    public void Zone_code_proposal_passes_over_a_number_whose_next_code_would_not_fit()
    {
        var longest = "KV" + new string('9', 48);

        // 47 nines still have a next code of exactly 50 characters.
        Assert.Equal("KV1" + new string('0', 47), Propose("KV" + new string('9', 47)));
        Assert.Equal("KV01", Propose(longest));
        Assert.Equal("KV08", Propose(longest, "KV07"));
    }

    [Fact]
    public void Zone_code_proposal_is_never_a_longest_code_already_in_use()
    {
        var nines = new string('9', 47);
        var (longest, below) = ("KV" + nines + "9", "KV" + nines + "8");

        Assert.Equal("KV" + nines + "7", Propose(longest, below, "KV" + nines + "6"));
        Assert.Equal("KV02", Propose(longest, below, "KV01"));
    }

    private static string Propose(params string[] existing) =>
        SequentialCodes.Next(SequentialCodes.ZonePrefix, SequentialCodes.ZoneDigits, SequentialCodes.ZoneCodeMaxLength, existing);

    private static LicenseWithReplacement License(
        DateOnly effective, DateOnly expiry, DateTimeOffset recorded, DateTimeOffset? replacedAt = null, Guid? zone = null) => new(
        new ZoneComplianceDocument { ZoneId = zone ?? Zone, EffectiveDate = effective, ExpiryDate = expiry, CreatedAt = recorded, DocumentNumber = "GP" },
        replacedAt);

    private sealed class Markers : IHasExpiryReminderMarkers
    {
        public DateOnly? ExpiryDate { get; init; }

        public DateTimeOffset? ExpiringReminderSentAt { get; set; }

        public DateTimeOffset? ExpiredReminderSentAt { get; set; }
    }
}
