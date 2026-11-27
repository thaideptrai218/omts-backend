using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>The status rule of care logs and material usage, and the date limits of a log.</summary>
public class CareRecordRulesTests
{
    private static Raft RaftOf(int raftStatus, int zoneStatus) =>
        new() { Code = "B1-01", StatusId = raftStatus, Zone = new FarmingZone { Code = "KV01", StatusId = zoneStatus } };

    [Theory]
    [InlineData(StatusIds.Usage.InUse, StatusIds.Usage.InUse, true)]
    [InlineData(StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, true)]
    [InlineData(StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, false)]
    [InlineData(StatusIds.Usage.Discontinued, StatusIds.Usage.Discontinued, false)]
    public void A_raft_In_Use_or_Discontinued_takes_care_records_when_its_zone_is_In_Use(int raftStatus, int zoneStatus, bool expected) =>
        Assert.Equal(expected, RaftViews.AcceptsCareRecords(RaftOf(raftStatus, zoneStatus)));

    [Fact]
    public void A_status_that_is_neither_is_refused()
    {
        Assert.False(RaftViews.AcceptsCareRecords(RaftOf(0, StatusIds.Usage.InUse)));
    }

    [Theory]
    [InlineData(StatusIds.CultivationCycle.Open, true)]
    [InlineData(StatusIds.CultivationCycle.Closed, false)]
    public void A_log_may_change_while_its_cycle_is_Open(int cycleStatus, bool expected)
    {
        var log = new CultivationLog { Raft = RaftOf(StatusIds.Usage.InUse, StatusIds.Usage.InUse), Cycle = new CultivationCycle { StatusId = cycleStatus } };

        Assert.Equal(expected, CultivationLogWriteGuard.MayChange(log));
    }

    [Theory]
    [InlineData(StatusIds.Usage.InUse, StatusIds.Usage.InUse, true)]
    [InlineData(StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, true)]
    [InlineData(StatusIds.Usage.Discontinued, StatusIds.Usage.Discontinued, false)]
    public void A_log_with_no_cycle_follows_the_raft_status_alone(int raftStatus, int zoneStatus, bool expected)
    {
        var log = new CultivationLog { Raft = RaftOf(raftStatus, zoneStatus) };

        Assert.Equal(expected, CultivationLogWriteGuard.MayChange(log));
    }

    private static readonly DateOnly Today = new(2026, 11, 27);

    private static AppError? Read(DateOnly? visit, DateOnly? earliest) =>
        CultivationLogValues.Read(visit, "REPAIR", null, [], otherFilePart: false, kept: 0, keysAreKnown: true, reason: null, reasonRequired: false, earliest, Today, out _);

    [Fact]
    public void A_log_on_a_raft_has_no_lower_bound_and_none_after_today()
    {
        Assert.Null(Read(new DateOnly(2020, 1, 1), earliest: null));
        Assert.Null(Read(Today, earliest: null));
        Assert.Equal(ErrorKind.Validation, Read(Today.AddDays(1), earliest: null)!.Kind);
    }

    [Fact]
    public void A_log_in_a_cycle_is_from_its_start_date_to_today()
    {
        var start = Today.AddDays(-10);

        Assert.Null(Read(start, start));
        Assert.Equal(ErrorKind.Validation, Read(start.AddDays(-1), start)!.Kind);
        Assert.Null(CultivationLogValues.ReadAgain(start, start));
        Assert.NotNull(CultivationLogValues.ReadAgain(start.AddDays(-1), start));
        Assert.Null(CultivationLogValues.ReadAgain(start.AddDays(-30), earliest: null));
    }
}
