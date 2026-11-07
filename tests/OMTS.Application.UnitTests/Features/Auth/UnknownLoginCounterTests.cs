using OMTS.Application.Features.Auth;

namespace OMTS.Application.UnitTests.Features.Auth;

public class UnknownLoginCounterTests
{
    [Fact]
    public void Counts_failures_per_identifier_ignoring_case()
    {
        using var counter = new UnknownLoginCounter();

        counter.RegisterFailure("ghost");
        counter.RegisterFailure("GHOST");

        Assert.Equal(3, counter.RegisterFailure("Ghost"));
        Assert.Equal(1, counter.RegisterFailure("other"));
    }

    // Anonymous callers can send any number of made-up identifiers; the counter never holds more than its limit.
    [Fact]
    public void Never_holds_more_identifiers_than_its_limit()
    {
        using var counter = new UnknownLoginCounter(maxIdentifiers: 50);

        for (var i = 0; i < 5_000; i++)
        {
            counter.RegisterFailure($"made-up-{i}");
        }

        Assert.InRange(counter.Count, 1, 50);
    }
}
