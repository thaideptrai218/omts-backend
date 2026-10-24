namespace OMTS.Application.UnitTests.Fakes;

/// <summary>A clock the test moves forward by hand (no timers).</summary>
public sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
