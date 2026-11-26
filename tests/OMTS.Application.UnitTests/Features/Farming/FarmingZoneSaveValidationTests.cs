using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>
/// A zone needs a code and a name: the location, the area and the pin are optional, and checked when they are sent. A zone with
/// a boundary keeps the pin the server set from it.
/// </summary>
public class FarmingZoneSaveValidationTests
{
    private static SaveFarmingZoneRequest Request(
        string? code = "KV01", string? name = "Vùng 1", string? location = null, decimal? latitude = null, decimal? longitude = null, decimal? area = null) =>
        new(code, name, location, latitude, longitude, area);

    [Fact]
    public void A_zone_with_only_a_code_and_a_name_is_valid()
    {
        var check = FarmingZoneService.Validate(Request(), out var values);

        Assert.True(check.IsValid);
        Assert.Null(values.Location);
        Assert.Null(values.Latitude);
        Assert.Null(values.Longitude);
        Assert.Null(values.Area);
    }

    [Fact]
    public void A_zone_with_a_boundary_does_not_check_the_pin_it_discards()
    {
        var check = FarmingZoneService.Validate(Request(latitude: 0, longitude: 0), out var values, ignorePin: true);

        Assert.True(check.IsValid);
        Assert.Null(values.Latitude);
        Assert.Null(values.Longitude);
    }

    [Fact]
    public void Code_and_name_stay_required()
    {
        var check = FarmingZoneService.Validate(Request(code: " ", name: null), out _);

        Assert.Equal(["Code", "Name"], check.Invalid);
    }

    [Fact]
    public void A_blank_location_is_null_and_a_given_one_is_trimmed_and_limited_to_500_characters()
    {
        FarmingZoneService.Validate(Request(location: "   "), out var blank);
        FarmingZoneService.Validate(Request(location: " Vân Đồn "), out var given);
        var longest = FarmingZoneService.Validate(Request(location: new string('a', 500)), out _);
        var tooLong = FarmingZoneService.Validate(Request(location: new string('a', 501)), out _);

        Assert.Null(blank.Location);
        Assert.Equal("Vân Đồn", given.Location);
        Assert.True(longest.IsValid);
        Assert.Equal(["Location"], tooLong.Invalid);
    }

    [Theory]
    [InlineData(0.01, true)]
    [InlineData(28.5, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void A_given_area_must_be_above_zero(double area, bool valid)
    {
        var check = FarmingZoneService.Validate(Request(area: (decimal)area), out _);

        Assert.Equal(valid, check.IsValid);
        Assert.Equal(valid ? [] : ["Area"], check.Invalid);
    }

    [Fact]
    public void A_pin_is_both_numbers_or_none()
    {
        var latitudeOnly = FarmingZoneService.Validate(Request(latitude: 21.05m), out _);
        var longitudeOnly = FarmingZoneService.Validate(Request(longitude: 107.35m), out _);
        var both = FarmingZoneService.Validate(Request(latitude: 21.05m, longitude: 107.35m), out var values);

        Assert.Equal(["Longitude"], latitudeOnly.Invalid);
        Assert.Equal(["Latitude"], longitudeOnly.Invalid);
        Assert.True(both.IsValid);
        Assert.Equal((21.05m, 107.35m), (values.Latitude, values.Longitude));
    }

    [Fact]
    public void A_given_pin_still_refuses_zero_zero_and_a_swapped_pair_but_only_warns_outside_Vietnam()
    {
        var zero = FarmingZoneService.Validate(Request(latitude: 0, longitude: 0), out _);
        var swapped = FarmingZoneService.Validate(Request(latitude: 107.35m, longitude: 21.05m), out _);
        var abroad = FarmingZoneService.Validate(Request(latitude: 48.85m, longitude: 2.35m), out _);

        Assert.Equal(["Latitude", "Longitude"], zero.Invalid);
        Assert.Equal(["Latitude", "Longitude"], swapped.Invalid);
        Assert.True(abroad.IsValid);
        Assert.Single(CoordinateRules.ForZone(48.85m, 2.35m));
    }

    [Fact]
    public void An_edit_without_a_pin_keeps_the_pin_of_a_zone_that_has_no_boundary()
    {
        var zone = new FarmingZone { Latitude = 21.05m, Longitude = 107.35m };
        FarmingZoneService.Validate(Request(), out var values);

        FarmingZoneService.Apply(zone, values, keepPin: FarmingZoneService.KeepsPin(zone, values));

        Assert.Equal(21.05m, zone.Latitude);
        Assert.Equal(107.35m, zone.Longitude);
    }

    [Fact]
    public void An_edit_that_sends_a_pin_moves_the_pin_of_a_zone_that_has_no_boundary()
    {
        var zone = new FarmingZone { Latitude = 21.05m, Longitude = 107.35m };
        FarmingZoneService.Validate(Request(latitude: 21.1m, longitude: 107.4m), out var values);

        FarmingZoneService.Apply(zone, values, keepPin: FarmingZoneService.KeepsPin(zone, values));

        Assert.Equal(21.1m, zone.Latitude);
        Assert.Equal(107.4m, zone.Longitude);
    }

    [Fact]
    public void An_edit_never_wipes_or_moves_the_pin_of_a_zone_that_has_a_boundary()
    {
        var zone = new FarmingZone { Latitude = 21.05m, Longitude = 107.35m, Area = 10m };
        FarmingZoneService.Validate(Request(name: "Vùng mới"), out var omitted);
        FarmingZoneService.Validate(Request(latitude: 10m, longitude: 106m, area: 3m), out var sent);

        FarmingZoneService.Apply(zone, omitted, keepPin: true);
        Assert.Equal((21.05m, 107.35m), (zone.Latitude, zone.Longitude));
        Assert.Equal("Vùng mới", zone.Name);
        Assert.Null(zone.Area); // the area is not server-owned: an edit without it clears it

        FarmingZoneService.Apply(zone, sent, keepPin: true);
        Assert.Equal((21.05m, 107.35m), (zone.Latitude, zone.Longitude));
        Assert.Equal(3m, zone.Area);
    }
}
