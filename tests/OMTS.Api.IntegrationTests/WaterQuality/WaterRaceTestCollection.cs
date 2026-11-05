namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// Tests with many parallel saves, each retrying up to 20 times. They run alone, as they can keep a small CI runner busy
/// for about a minute, and the tests that measure time around them then fail.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WaterRaceTestCollection
{
    public const string Name = "Water correction races";
}
