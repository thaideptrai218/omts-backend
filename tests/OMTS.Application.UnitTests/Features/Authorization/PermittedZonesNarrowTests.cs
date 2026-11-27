using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.UnitTests.Features.Authorization;

/// <summary>The zones a list is asked for, and the part of them the caller may read.</summary>
public sealed class PermittedZonesNarrowTests
{
    private readonly Guid a = Guid.NewGuid();
    private readonly Guid b = Guid.NewGuid();
    private readonly Guid c = Guid.NewGuid();

    private static PermittedZones Only(params Guid[] zones) => new(false, zones.ToHashSet());

    [Fact]
    public void Nothing_chosen_keeps_every_permitted_zone()
    {
        Assert.Null(PermittedZones.Everywhere.Narrow(null).ZoneIds);
        Assert.Null(PermittedZones.Everywhere.Narrow([]).ZoneIds);
        Assert.True(new HashSet<Guid> { a, b }.SetEquals(Only(a, b).Narrow([]).ZoneIds!));
        Assert.False(Only(a, b).Narrow(null).IsEmpty);
    }

    [Fact]
    public void A_caller_with_no_zone_has_an_empty_result_whether_or_not_zones_were_chosen()
    {
        Assert.True(PermittedZones.None.Narrow(null).IsEmpty);
        Assert.True(PermittedZones.None.Narrow([a]).IsEmpty);
        Assert.Empty(PermittedZones.None.Narrow([a]).ZoneIds!);
    }

    [Fact]
    public void A_caller_of_every_zone_gets_the_chosen_zones_as_they_are()
    {
        var narrowed = PermittedZones.Everywhere.Narrow([a, b]);

        Assert.True(new HashSet<Guid> { a, b }.SetEquals(narrowed.ZoneIds!));
        Assert.False(narrowed.IsEmpty);
    }

    [Fact]
    public void A_zone_the_caller_may_not_read_is_left_out_without_a_refusal()
    {
        var narrowed = Only(a, b).Narrow([b, c]);

        Assert.Equal([b], narrowed.ZoneIds!);
        Assert.False(narrowed.IsEmpty);
    }

    [Fact]
    public void Chosen_zones_none_of_which_is_permitted_leave_an_empty_result_never_every_zone()
    {
        var narrowed = Only(a).Narrow([b, c]);

        Assert.True(narrowed.IsEmpty);
        Assert.NotNull(narrowed.ZoneIds);
        Assert.Empty(narrowed.ZoneIds);
    }

    [Fact]
    public void The_single_zone_and_the_zone_ids_are_one_distinct_set()
    {
        var chosen = PermittedZones.Choose(c, [a, b, a, c]);

        Assert.True(chosen.IsSuccess);
        Assert.Equal([a, b, c], chosen.Value!);
    }

    [Fact]
    public void Nothing_asked_is_an_empty_choice()
    {
        Assert.Empty(PermittedZones.Choose(null, null).Value!);
        Assert.Equal([a], PermittedZones.Choose(a, null).Value!);
    }

    [Fact]
    public void More_than_two_hundred_distinct_zone_ids_are_MSG_GEN_01_on_ZoneIds_and_two_hundred_are_fine()
    {
        var many = Enumerable.Range(0, PermittedZones.MaxChosen + 1).Select(_ => Guid.NewGuid()).ToArray();

        var refused = PermittedZones.Choose(null, many);

        Assert.Equal(ErrorKind.Validation, refused.Error!.Kind);
        Assert.Equal("ZoneIds", refused.Error.Details!["field"]);
        Assert.True(PermittedZones.Choose(null, many.Take(PermittedZones.MaxChosen).ToArray()).IsSuccess);
        // Repeats of one zone are not many zones.
        Assert.True(PermittedZones.Choose(null, Enumerable.Repeat(a, 500).ToArray()).IsSuccess);
    }
}
