using OMTS.Application.Common.Concurrency;
using OMTS.Application.UnitTests.Fakes;

namespace OMTS.Application.UnitTests.Common;

/// <summary>NFR-13 editing indicator: no lock, cleared on cancel/save or 5 minutes after the last heartbeat.</summary>
public class EditingPresenceTrackerTests
{
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 10, 23, 2, 0, 0, TimeSpan.Zero));
    private readonly Guid _record = Guid.NewGuid();
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    [Fact]
    public void Others_see_the_editor_with_the_start_time_and_never_themselves()
    {
        var tracker = new EditingPresenceTracker(_clock);
        var started = _clock.GetUtcNow();
        tracker.Heartbeat(_record, _alice, "Alice");

        var seenByBob = Assert.Single(tracker.EditorsOf(_record, _bob));
        Assert.Equal(new RecordEditor(_alice, "Alice", started), seenByBob);
        Assert.Empty(tracker.EditorsOf(_record, _alice));
        Assert.Empty(tracker.EditorsOf(Guid.NewGuid(), _bob));
    }

    [Fact]
    public void Indicator_expires_five_minutes_after_the_last_heartbeat()
    {
        var tracker = new EditingPresenceTracker(_clock);
        var started = _clock.GetUtcNow();
        tracker.Heartbeat(_record, _alice, "Alice");

        _clock.Advance(TimeSpan.FromMinutes(4));
        tracker.Heartbeat(_record, _alice, "Alice");
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(started, Assert.Single(tracker.EditorsOf(_record, _bob)).StartedAt);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(tracker.EditorsOf(_record, _bob));
    }

    [Fact]
    public void Stop_clears_only_the_callers_entry()
    {
        var tracker = new EditingPresenceTracker(_clock);
        tracker.Heartbeat(_record, _alice, "Alice");
        _clock.Advance(TimeSpan.FromSeconds(30));
        tracker.Heartbeat(_record, _bob, "Bob");

        Assert.Equal(["Alice", "Bob"], tracker.EditorsOf(_record, null).Select(e => e.Name));
        tracker.Stop(_record, _alice);

        Assert.Equal(["Bob"], tracker.EditorsOf(_record, null).Select(e => e.Name));
    }
}
