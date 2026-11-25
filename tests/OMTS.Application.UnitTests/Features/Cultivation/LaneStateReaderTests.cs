using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>The lane days of many rafts read together are those read one raft at a time, and cost the same few reads however many rafts there are.</summary>
public class LaneStateReaderTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    private sealed class Data
    {
        public List<CycleSpan> Cycles { get; } = [];

        public List<LaneSeeding> Seedings { get; } = [];

        public List<LaneHarvest> Harvests { get; } = [];

        public List<LaneProgressStep> Steps { get; } = [];

        public Dictionary<string, int> Reads { get; } = [];

        public LaneStateReader Reader() => new(
            StubProxy.Create<ICultivationCycleQuery>(h =>
            {
                h["CycleOnDateAsync"] = a => Counted("CycleOnDateAsync", () => Cycles.Where(c => c.RaftId == (Guid)a[0]! && c.StartDate <= (DateOnly)a[1]! && (c.CloseDate == null || c.CloseDate >= (DateOnly)a[1]!)).OrderByDescending(c => c.StartDate).FirstOrDefault());
                h["CyclesOnDateAsync"] = a => Counted("CyclesOnDateAsync", () => (IReadOnlyDictionary<Guid, CycleSpan>)Cycles
                    .Where(c => ((IReadOnlyCollection<Guid>)a[0]!).Contains(c.RaftId) && c.StartDate <= (DateOnly)a[1]! && (c.CloseDate == null || c.CloseDate >= (DateOnly)a[1]!))
                    .GroupBy(c => c.RaftId).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.StartDate).First()));
            }),
            StubProxy.Create<ISeedingRecordRepository>(h =>
            {
                h["LaneSeedingsAsync"] = a => Counted("LaneSeedingsAsync", () => (IReadOnlyList<LaneSeeding>)Seedings.Where(s => s.CycleId == (Guid)a[0]!).ToList());
                h["LaneSeedingsOfCyclesAsync"] = a => Counted("LaneSeedingsOfCyclesAsync", () => (IReadOnlyList<LaneSeeding>)Seedings.Where(s => ((IReadOnlyCollection<Guid>)a[0]!).Contains(s.CycleId)).ToList());
            }),
            StubProxy.Create<IHarvestLotRepository>(h =>
            {
                h["LaneHarvestsAsync"] = a => Counted("LaneHarvestsAsync", () => (IReadOnlyList<LaneHarvest>)Harvests.Where(s => s.CycleId == (Guid)a[0]!).ToList());
                h["LaneHarvestsOfCyclesAsync"] = a => Counted("LaneHarvestsOfCyclesAsync", () => (IReadOnlyList<LaneHarvest>)Harvests.Where(s => ((IReadOnlyCollection<Guid>)a[0]!).Contains(s.CycleId)).ToList());
            }),
            StubProxy.Create<ILaneProgressRepository>(h =>
            {
                h["StepsAsync"] = a => Counted("StepsAsync", () => (IReadOnlyList<LaneProgressStep>)Steps.Where(s => s.CycleId == (Guid)a[0]!).OrderBy(s => s.At).ToList());
                h["StepsOfCyclesAsync"] = a => Counted("StepsOfCyclesAsync", () => (IReadOnlyList<LaneProgressStep>)Steps.Where(s => ((IReadOnlyCollection<Guid>)a[0]!).Contains(s.CycleId)).OrderBy(s => s.At).ToList());
            }));

        private object? Counted(string name, Func<object?> read)
        {
            Reads[name] = Reads.GetValueOrDefault(name) + 1;
            return read();
        }
    }

    private static Raft NewRaft(int lanes)
    {
        var raft = new Raft { Code = "B" + Guid.NewGuid().ToString("N")[..4] };
        for (var number = 1; number <= lanes; number++)
        {
            raft.Lanes.Add(new Lane { LaneNumber = number, RaftId = raft.Id });
        }

        return raft;
    }

    [Fact]
    public async Task Rafts_read_together_have_the_lane_days_they_have_read_one_by_one_in_four_reads()
    {
        var data = new Data();
        var growing = NewRaft(3);
        var harvested = NewRaft(2);
        var corrected = NewRaft(2);
        var idle = NewRaft(2);
        var closed = NewRaft(2);
        foreach (var raft in new[] { growing, harvested, corrected, idle, closed })
        {
            if (raft == idle)
            {
                continue;
            }

            var cycle = new CycleSpan(Guid.NewGuid(), raft.Id, Today.AddDays(-60), raft == closed ? Today.AddDays(-30) : null);
            data.Cycles.Add(cycle);
            data.Seedings.AddRange(raft.Lanes.Select(l => new LaneSeeding(cycle.Id, l.Id, cycle.StartDate.AddDays(2), 100m, DateTimeOffset.UtcNow.AddDays(-58), Guid.NewGuid())));
        }

        var harvestedCycle = data.Cycles.Single(c => c.RaftId == harvested.Id);
        data.Harvests.Add(new LaneHarvest(harvestedCycle.Id, harvested.Lanes.First().Id, Today.AddDays(-3), IsFinished: true, 100m, DateTimeOffset.UtcNow.AddDays(-3), Guid.NewGuid()));
        var correctedCycle = data.Cycles.Single(c => c.RaftId == corrected.Id);
        data.Steps.Add(new LaneProgressStep(correctedCycle.Id, corrected.Lanes.First().Id, DateTimeOffset.UtcNow.AddDays(-1), Today.AddDays(-1), Guid.NewGuid(), "Recount", 100m, null, 60m, 20m));
        var reader = data.Reader();
        var rafts = new[] { growing, harvested, corrected, idle, closed };

        var together = await reader.ReadManyAsync(rafts, Today, default);
        var togetherReads = data.Reads.ToDictionary(r => r.Key, r => r.Value);
        data.Reads.Clear();
        var oneByOne = new Dictionary<Guid, LaneDay>();
        foreach (var raft in rafts)
        {
            oneByOne[raft.Id] = await reader.ReadAsync(raft, Today, default);
        }

        Assert.Equal(new Dictionary<string, int> { ["CyclesOnDateAsync"] = 1, ["LaneSeedingsOfCyclesAsync"] = 1, ["LaneHarvestsOfCyclesAsync"] = 1, ["StepsOfCyclesAsync"] = 1 }, togetherReads);
        Assert.True(data.Reads["CycleOnDateAsync"] > 1);
        Assert.Equal(rafts.Select(r => r.Id).Order(), together.Keys.Order());
        foreach (var raft in rafts)
        {
            var (a, b) = (together[raft.Id], oneByOne[raft.Id]);
            Assert.Equal(b.Cycle, a.Cycle);
            Assert.Equal(b.States.OrderBy(s => s.Key), a.States.OrderBy(s => s.Key));
            Assert.Equal(b.LanesWithRecords.Order(), a.LanesWithRecords.Order());
            Assert.Equal(b.Percents!.OrderBy(p => p.Key), a.Percents!.OrderBy(p => p.Key));
            Assert.Equal(b.Steps, a.Steps);
        }

        Assert.Equal(LaneState.Harvested, together[harvested.Id].States[harvested.Lanes.First().Id]);
        Assert.Equal(LaneState.Growing, together[growing.Id].States[growing.Lanes.First().Id]);
        Assert.Null(together[idle.Id].Cycle);
        Assert.All(together[idle.Id].States.Values, s => Assert.Equal(LaneState.NotSeeded, s));
    }

    [Fact]
    public async Task No_rafts_read_nothing()
    {
        var data = new Data();

        var days = await data.Reader().ReadManyAsync([], Today, default);

        Assert.Empty(days);
    }
}
