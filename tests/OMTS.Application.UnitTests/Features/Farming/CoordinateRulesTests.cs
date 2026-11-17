using OMTS.Application.Features.Farming;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>PM decision 11/11/2026: (0, 0) and swapped pairs are refused; a pin outside Vietnam or a raft far from its zone only warns.</summary>
public class CoordinateRulesTests
{
    [Fact]
    public void Zero_zero_is_refused() => Assert.NotNull(CoordinateRules.RefusalHint(0m, 0m));

    [Fact]
    public void A_swapped_pair_in_vietnam_is_refused_with_a_hint() =>
        Assert.Contains("swapped", CoordinateRules.RefusalHint(106.6m, 10.8m)); // Ho Chi Minh City, longitude first

    [Theory]
    [InlineData(10.8, 106.6)]
    [InlineData(21.07, 107.43)] // Ha Long Bay
    [InlineData(35.0, 139.0)] // Tokyo: outside Vietnam but not a swap: only a warning
    public void A_correct_or_foreign_pair_is_not_refused(double latitude, double longitude) =>
        Assert.Null(CoordinateRules.RefusalHint((decimal)latitude, (decimal)longitude));

    [Fact]
    public void A_zone_pin_outside_vietnam_warns()
    {
        Assert.Empty(CoordinateRules.ForZone(21.07m, 107.43m));
        Assert.Equal(CoordinateRules.OutsideVietnamCode, Assert.Single(CoordinateRules.ForZone(35m, 139m)).Code);
        Assert.Empty(CoordinateRules.ForZone(null, null));
    }

    [Fact]
    public void A_raft_farther_than_5_km_from_its_zone_warns_with_the_distance()
    {
        // 0.01 degree of latitude is about 1.11 km.
        Assert.Empty(CoordinateRules.ForRaft(21.08m, 107.43m, 21.07m, 107.43m));
        var warning = Assert.Single(CoordinateRules.ForRaft(21.13m, 107.43m, 21.07m, 107.43m));
        Assert.Equal(CoordinateRules.FarFromZoneCode, warning.Code);
        Assert.InRange((double)warning.Details!["distanceKm"]!, 6.5, 6.8);
    }

    [Fact]
    public void A_raft_without_a_pin_never_warns() => Assert.Empty(CoordinateRules.ForRaft(null, null, 21.07m, 107.43m));

    [Fact]
    public void Distance_is_the_haversine_great_circle() =>
        Assert.InRange(CoordinateRules.DistanceKm(10.8m, 106.6m, 21.03m, 105.85m), 1100, 1180); // Ho Chi Minh City to Hanoi
}
