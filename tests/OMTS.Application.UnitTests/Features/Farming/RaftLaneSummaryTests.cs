using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>The lane summary of the raft list and the open cycle of the raft detail: what each lane counts as, and the days of farming.</summary>
public class RaftLaneSummaryTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    // 03:00 UTC on 27/11 is 10:00 on 27/11 in Vietnam.
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 3, 0, 0, TimeSpan.Zero);

    private static Lane NewLane(int number, int statusId = StatusIds.Lane.InUse, bool damaged = false, DateOnly? outOfServiceOn = null) =>
        new() { LaneNumber = number, StatusId = statusId, IsDamaged = damaged, OutOfServiceOn = outOfServiceOn };

    private static Dictionary<Guid, LaneState> States(params (Lane Lane, LaneState State)[] states) => states.ToDictionary(s => s.Lane.Id, s => s.State);

    [Fact]
    public void Growing_and_being_harvested_lanes_are_farming_and_the_other_states_have_their_own_count()
    {
        var lanes = Enumerable.Range(1, 5).Select(n => NewLane(n)).ToList();

        var summary = RaftLaneSummaryRules.Of(lanes, States(
            (lanes[0], LaneState.Growing), (lanes[1], LaneState.BeingHarvested), (lanes[2], LaneState.Harvested), (lanes[3], LaneState.NotSeeded), (lanes[4], LaneState.OutOfService)));

        Assert.Equal(new RaftLaneSummary(Farming: 2, NotSeeded: 1, Harvested: 1, Damaged: 0, OutOfService: 1), summary);
    }

    [Fact]
    public void Out_of_service_wins_over_damaged_and_both_win_over_the_state()
    {
        var lanes = new List<Lane>
        {
            NewLane(1, damaged: true),
            NewLane(2, damaged: true, outOfServiceOn: Today.AddDays(-2)),
            NewLane(3, outOfServiceOn: Today.AddDays(-1)),
            NewLane(4),
        };

        var summary = RaftLaneSummaryRules.Of(lanes, States((lanes[0], LaneState.Growing), (lanes[1], LaneState.Growing), (lanes[2], LaneState.NotSeeded), (lanes[3], LaneState.Growing)));

        Assert.Equal(new RaftLaneSummary(Farming: 1, NotSeeded: 0, Harvested: 0, Damaged: 1, OutOfService: 2), summary);
    }

    [Fact]
    public void A_removed_lane_is_not_counted_and_a_lane_without_a_state_is_not_seeded()
    {
        var lanes = new List<Lane> { NewLane(1), NewLane(2), NewLane(3, StatusIds.Lane.Removed) };

        var summary = RaftLaneSummaryRules.Of(lanes, States((lanes[2], LaneState.Growing)));

        Assert.Equal(new RaftLaneSummary(0, 2, 0, 0, 0), summary);
    }

    [Fact]
    public void A_raft_without_a_lane_in_use_has_no_summary()
    {
        Assert.Null(RaftLaneSummaryRules.Of([], new Dictionary<Guid, LaneState>()));
        Assert.Null(RaftLaneSummaryRules.Of([NewLane(1, StatusIds.Lane.Removed)], new Dictionary<Guid, LaneState>()));
    }

    private sealed class Data
    {
        public List<Raft> Rafts { get; } = [];

        public List<CycleSpan> Cycles { get; } = [];

        public List<LaneSeeding> Seedings { get; } = [];

        public List<LaneHarvest> Harvests { get; } = [];

        public List<CycleSeedSource> Sources { get; } = [];

        public Dictionary<string, int> Reads { get; } = [];

        public RaftLiveReader Reader(DateTimeOffset now)
        {
            var laneStates = new LaneStateReader(
                StubProxy.Create<ICultivationCycleQuery>(h =>
                    h["CyclesOnDateAsync"] = a => Counted("CyclesOnDateAsync", () => (IReadOnlyDictionary<Guid, CycleSpan>)Cycles
                        .Where(c => ((IReadOnlyCollection<Guid>)a[0]!).Contains(c.RaftId) && c.StartDate <= (DateOnly)a[1]! && (c.CloseDate == null || c.CloseDate >= (DateOnly)a[1]!))
                        .ToDictionary(c => c.RaftId))),
                StubProxy.Create<ISeedingRecordRepository>(h =>
                    h["LaneSeedingsOfCyclesAsync"] = a => Counted("LaneSeedingsOfCyclesAsync", () => (IReadOnlyList<LaneSeeding>)Seedings.Where(s => ((IReadOnlyCollection<Guid>)a[0]!).Contains(s.CycleId)).ToList())),
                StubProxy.Create<IHarvestLotRepository>(h =>
                    h["LaneHarvestsOfCyclesAsync"] = a => Counted("LaneHarvestsOfCyclesAsync", () => (IReadOnlyList<LaneHarvest>)Harvests.Where(s => ((IReadOnlyCollection<Guid>)a[0]!).Contains(s.CycleId)).ToList())),
                StubProxy.Create<ILaneProgressRepository>(h =>
                    h["StepsOfCyclesAsync"] = a => Counted("StepsOfCyclesAsync", () => (IReadOnlyList<LaneProgressStep>)new List<LaneProgressStep>())));
            var seedLabels = new CycleSeedLabels(StubProxy.Create<INurseryBatchRepository>(h =>
                h["SeedSourcesAsync"] = a => Counted("SeedSourcesAsync", () => (IReadOnlyList<CycleSeedSource>)Sources.Where(s => ((IReadOnlyCollection<Guid>)a[0]!).Contains(s.CycleId)).ToList())));
            var rafts = StubProxy.Create<IRaftRepository>(h =>
                h["GetWithLanesByIdsAsync"] = a => Counted("GetWithLanesByIdsAsync", () => (IReadOnlyList<Raft>)Rafts.Where(r => ((IReadOnlyCollection<Guid>)a[0]!).Contains(r.Id)).ToList()));
            return new RaftLiveReader(rafts, laneStates, seedLabels, new FixedTimeProvider(now));
        }

        public Raft AddRaft(int lanes)
        {
            var raft = new Raft { Code = "B" + Guid.NewGuid().ToString("N")[..4] };
            for (var number = 1; number <= lanes; number++)
            {
                raft.Lanes.Add(NewLane(number));
            }

            Rafts.Add(raft);
            return raft;
        }

        public CycleSpan AddOpenCycle(Raft raft, DateOnly start)
        {
            var cycle = new CycleSpan(Guid.NewGuid(), raft.Id, start, null);
            Cycles.Add(cycle);
            return cycle;
        }

        public void Seed(CycleSpan cycle, Lane lane) =>
            Seedings.Add(new LaneSeeding(cycle.Id, lane.Id, cycle.StartDate, 100m, DateTimeOffset.UtcNow.AddDays(-30), Guid.NewGuid()));

        public void Harvest(CycleSpan cycle, Lane lane, bool finished) =>
            Harvests.Add(new LaneHarvest(cycle.Id, lane.Id, Today.AddDays(-1), finished, finished ? 100m : 40m, DateTimeOffset.UtcNow.AddDays(-1), Guid.NewGuid()));

        private object? Counted(string name, Func<object?> read)
        {
            Reads[name] = Reads.GetValueOrDefault(name) + 1;
            return read();
        }
    }

    [Fact]
    public async Task The_list_summaries_follow_the_lane_states_of_each_raft_and_cost_the_same_reads_for_any_number_of_rafts()
    {
        var data = new Data();
        var farming = data.AddRaft(4);
        var idle = data.AddRaft(2);
        var bare = data.AddRaft(0);
        var cycle = data.AddOpenCycle(farming, Today.AddDays(-20));
        data.Seed(cycle, farming.Lanes.First(l => l.LaneNumber == 1));
        data.Seed(cycle, farming.Lanes.First(l => l.LaneNumber == 2));
        data.Seed(cycle, farming.Lanes.First(l => l.LaneNumber == 3));
        data.Harvest(cycle, farming.Lanes.First(l => l.LaneNumber == 2), finished: false);
        data.Harvest(cycle, farming.Lanes.First(l => l.LaneNumber == 3), finished: true);
        farming.Lanes.First(l => l.LaneNumber == 4).IsDamaged = true;

        var summaries = await data.Reader(Now).SummariesAsync([farming.Id, idle.Id, bare.Id, Guid.NewGuid()], default);

        Assert.Equal(new RaftLaneSummary(Farming: 2, NotSeeded: 0, Harvested: 1, Damaged: 1, OutOfService: 0), summaries[farming.Id]);
        Assert.Equal(new RaftLaneSummary(0, 2, 0, 0, 0), summaries[idle.Id]);
        Assert.Null(summaries[bare.Id]);
        Assert.Equal(3, summaries.Count);
        Assert.Equal(1, data.Reads["GetWithLanesByIdsAsync"]);
        Assert.Equal(1, data.Reads["CyclesOnDateAsync"]);
        Assert.Equal(1, data.Reads["LaneSeedingsOfCyclesAsync"]);
    }

    [Fact]
    public async Task An_empty_list_reads_nothing()
    {
        var data = new Data();

        Assert.Empty(await data.Reader(Now).SummariesAsync([], default));
        Assert.Empty(data.Reads);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(9, 10)]
    public async Task Days_farming_count_the_start_day_and_today(int startedDaysAgo, int expectedDays)
    {
        var data = new Data();
        var raft = data.AddRaft(2);
        data.AddOpenCycle(raft, Today.AddDays(-startedDaysAgo));

        var cycle = await data.Reader(Now).CurrentCycleAsync(raft, default);

        Assert.Equal(expectedDays, cycle!.DaysFarming);
    }

    [Fact]
    public async Task Days_farming_use_the_vietnam_date_late_in_the_evening_utc()
    {
        var data = new Data();
        var raft = data.AddRaft(1);
        data.AddOpenCycle(raft, Today);

        // 20:00 UTC on 26/11 is 03:00 on 27/11 in Vietnam: a cycle started on 27/11 is on its first day.
        var cycle = await data.Reader(new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero)).CurrentCycleAsync(raft, default);

        Assert.Equal(1, cycle!.DaysFarming);
    }

    [Fact]
    public async Task The_current_cycle_has_its_lane_summary_and_the_distinct_seed_names()
    {
        var data = new Data();
        var raft = data.AddRaft(3);
        var span = data.AddOpenCycle(raft, Today.AddDays(-5));
        data.Seed(span, raft.Lanes.First());
        data.Sources.Add(new CycleSeedSource(span.Id, Guid.NewGuid(), "PN000001", 1, "Hau giong A", null, 500m, FromBatch: false));
        data.Sources.Add(new CycleSeedSource(span.Id, Guid.NewGuid(), "PN000002", 1, "Hau giong B", null, 900m, FromBatch: false));
        data.Sources.Add(new CycleSeedSource(span.Id, Guid.NewGuid(), "PN000003", 1, "Hau giong B", null, 100m, FromBatch: false));

        var cycle = await data.Reader(Now).CurrentCycleAsync(raft, default);

        Assert.Equal(span.Id, cycle!.Id);
        Assert.Equal(span.StartDate, cycle.StartDate);
        Assert.Equal(["Hau giong B", "Hau giong A"], cycle.SeedNames);
        Assert.Equal(new RaftLaneSummary(1, 2, 0, 0, 0), cycle.LaneSummary);
    }

    [Fact]
    public async Task A_cycle_without_seed_has_no_seed_names()
    {
        var data = new Data();
        var raft = data.AddRaft(1);
        data.AddOpenCycle(raft, Today);

        var cycle = await data.Reader(Now).CurrentCycleAsync(raft, default);

        Assert.Empty(cycle!.SeedNames);
    }

    [Fact]
    public async Task A_raft_without_an_open_cycle_has_no_current_cycle()
    {
        var data = new Data();
        var raft = data.AddRaft(2);
        data.Cycles.Add(new CycleSpan(Guid.NewGuid(), raft.Id, Today.AddDays(-40), Today.AddDays(-10)));

        Assert.Null(await data.Reader(Now).CurrentCycleAsync(raft, default));
    }
}
