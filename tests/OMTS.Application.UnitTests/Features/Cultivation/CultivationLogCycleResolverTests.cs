using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>The cycle of a care log is read from its visit date, as for material usage, and the raft takes care records In Use or Discontinued in a zone In Use.</summary>
public class CultivationLogCycleResolverTests
{
    private static readonly DateOnly Visit = new(2026, 11, 20);

    private sealed class Rig
    {
        public Raft Raft { get; } = new() { Code = "B1-01" };

        public CycleSpan? Span { get; set; }

        /// <summary>The cycle row as it is when read again for the save; null means it is gone.</summary>
        public CultivationCycle? Row { get; set; }

        public List<object> Marked { get; } = [];

        public CultivationLogCycleResolver Resolver() => new(
            StubProxy.Create<ICultivationCycleQuery>(h => h["CycleOnDateAsync"] = a => Span is { } span && (Guid)a[0]! == Raft.Id && span.IsOpenOn((DateOnly)a[1]!) ? span : null),
            StubProxy.Create<ICultivationCycleRepository>(h => h["GetForEditAsync"] = _ => Row),
            StubProxy.Create<IUnitOfWork>(h => h["MarkChanged"] = a =>
            {
                Marked.Add(a[0]!);
                return null;
            }));

        public CultivationCycle Cycle(DateOnly start, DateOnly? close = null)
        {
            var cycle = new CultivationCycle
            {
                RaftId = Raft.Id, StartDate = start, CloseDate = close,
                StatusId = close is null ? StatusIds.CultivationCycle.Open : StatusIds.CultivationCycle.Closed,
            };
            Span = new CycleSpan(cycle.Id, Raft.Id, start, close);
            Row = cycle;
            return cycle;
        }
    }

    [Fact]
    public async Task A_date_in_no_cycle_leaves_the_log_without_one_and_holds_the_raft_row()
    {
        var rig = new Rig();

        var resolved = await rig.Resolver().ResolveAsync(rig.Raft, Visit, CancellationToken.None);

        Assert.Null(resolved.Cycle);
        Assert.Null(resolved.Refusal);
        Assert.Same(rig.Raft, Assert.Single(rig.Marked));
    }

    [Fact]
    public async Task A_date_before_the_open_cycle_started_has_no_cycle()
    {
        var rig = new Rig();
        rig.Cycle(Visit.AddDays(1));

        var resolved = await rig.Resolver().ResolveAsync(rig.Raft, Visit, CancellationToken.None);

        Assert.Null(resolved.Cycle);
        Assert.Null(resolved.Refusal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_date_from_the_start_day_of_the_open_cycle_belongs_to_it_and_holds_the_cycle_row(int startOffset)
    {
        var rig = new Rig();
        var cycle = rig.Cycle(Visit.AddDays(startOffset));

        var resolved = await rig.Resolver().ResolveAsync(rig.Raft, Visit, CancellationToken.None);

        Assert.Same(cycle, resolved.Cycle);
        Assert.Null(resolved.Refusal);
        Assert.Same(cycle, Assert.Single(rig.Marked));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task A_date_inside_a_closed_cycle_is_refused_with_msg_gen_09_naming_it_and_holds_nothing(int closeOffset)
    {
        var rig = new Rig();
        var cycle = rig.Cycle(Visit.AddDays(-10), Visit.AddDays(closeOffset));

        var resolved = await rig.Resolver().ResolveAsync(rig.Raft, Visit, CancellationToken.None);

        Assert.Null(resolved.Cycle);
        var refusal = Assert.IsType<AppError>(resolved.Refusal);
        Assert.Equal(ErrorKind.Conflict, refusal.Kind);
        Assert.Equal("MSG-GEN-09", refusal.Message.Code);
        Assert.Contains("is Closed and cannot be used for this action.", refusal.Message.Text);
        Assert.Contains("B1-01", refusal.Message.Text);
        Assert.Equal(cycle.Id, refusal.Details!["cycleId"]);
        Assert.Empty(rig.Marked);
    }

    [Fact]
    public async Task The_close_day_still_belongs_to_the_closed_cycle_and_the_next_day_is_free()
    {
        var rig = new Rig();
        rig.Cycle(Visit.AddDays(-10), Visit);

        Assert.NotNull((await rig.Resolver().ResolveAsync(rig.Raft, Visit, CancellationToken.None)).Refusal);
        var after = await rig.Resolver().ResolveAsync(rig.Raft, Visit.AddDays(1), CancellationToken.None);
        Assert.Null(after.Refusal);
        Assert.Null(after.Cycle);
    }

    [Fact]
    public async Task A_cycle_closed_since_the_first_look_refuses_instead_of_taking_the_log()
    {
        var rig = new Rig();
        var open = rig.Cycle(Visit.AddDays(-5));
        (open.CloseDate, open.StatusId) = (Visit, StatusIds.CultivationCycle.Closed);

        var resolved = await rig.Resolver().ResolveAsync(rig.Raft, Visit, CancellationToken.None);

        Assert.Null(resolved.Cycle);
        Assert.Equal("MSG-GEN-09", resolved.Refusal!.Message.Code);
        Assert.Empty(rig.Marked);
    }

    [Fact]
    public async Task A_cycle_cancelled_since_the_first_look_leaves_the_log_without_one()
    {
        var rig = new Rig();
        rig.Cycle(Visit.AddDays(-5));
        rig.Row = null;

        var resolved = await rig.Resolver().ResolveAsync(rig.Raft, Visit, CancellationToken.None);

        Assert.Null(resolved.Cycle);
        Assert.Null(resolved.Refusal);
        Assert.Same(rig.Raft, Assert.Single(rig.Marked));
    }
}
