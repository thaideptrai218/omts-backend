using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Results;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Application.Features.AdministrativeUnits;

namespace OMTS.Application.UnitTests.Features.AdministrativeUnits;

public sealed class AdministrativeUnitServiceTests
{
    private sealed class TestClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    [Fact]
    public async Task The_provinces_are_read_once_and_kept()
    {
        var source = new FakeAdministrativeUnitSource();
        var units = AddressTestData.Service(source);

        var first = await units.ProvincesAsync(CancellationToken.None);
        var second = await units.ProvincesAsync(CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(5, second.Value!.Count);
        Assert.Equal(1, source.ProvinceCalls);
        // Sorted by the name without its prefix and diacritics.
        Assert.Equal(["Tỉnh An Giang", "Thành phố Đà Nẵng", "Thành phố Hà Nội", "Thành phố Hồ Chí Minh", "Thành phố Huế"], second.Value.Select(p => p.Name));
    }

    [Fact]
    public async Task The_wards_of_each_province_are_kept_apart()
    {
        var source = new FakeAdministrativeUnitSource();
        var units = AddressTestData.Service(source);

        await units.WardsAsync(1, CancellationToken.None);
        await units.WardsAsync(1, CancellationToken.None);
        await units.WardsAsync(79, CancellationToken.None);

        Assert.Equal(2, source.WardCalls);
    }

    [Fact]
    public async Task A_list_that_is_down_answers_service_unavailable_and_is_not_kept()
    {
        var source = new FakeAdministrativeUnitSource { Down = true };
        var units = AddressTestData.Service(source);

        var down = await units.ProvincesAsync(CancellationToken.None);
        Assert.Equal(ErrorKind.ServiceUnavailable, down.Error!.Kind);
        Assert.Contains("Không tải được", down.Error.Message.Text);

        source.Down = false;
        var up = await units.ProvincesAsync(CancellationToken.None);

        Assert.True(up.IsSuccess);
        Assert.Equal(2, source.ProvinceCalls);
    }

    [Fact]
    public async Task The_wards_of_a_code_that_is_not_a_province_are_not_found()
    {
        var units = AddressTestData.Service(new FakeAdministrativeUnitSource());

        var wards = await units.WardsAsync(999, CancellationToken.None);

        Assert.Equal(ErrorKind.NotFound, wards.Error!.Kind);
    }

    [Fact]
    public async Task The_list_is_read_again_after_a_day()
    {
        var source = new FakeAdministrativeUnitSource();
        var clock = new TestClock();
        var units = AddressTestData.Service(source, new MemoryCache(new MemoryCacheOptions { Clock = clock }));

        await units.ProvincesAsync(CancellationToken.None);
        clock.Advance(AdministrativeUnitService.CacheTime - TimeSpan.FromMinutes(1));
        await units.ProvincesAsync(CancellationToken.None);
        Assert.Equal(1, source.ProvinceCalls);

        clock.Advance(TimeSpan.FromMinutes(2));
        await units.ProvincesAsync(CancellationToken.None);
        Assert.Equal(2, source.ProvinceCalls);
    }

    [Theory]
    [InlineData(1, 10, AddressCheckOutcome.Valid)]
    [InlineData(1, null, AddressCheckOutcome.Valid)]
    [InlineData(1, 300, AddressCheckOutcome.UnknownWard)]
    [InlineData(1, 99999, AddressCheckOutcome.UnknownWard)]
    [InlineData(999, null, AddressCheckOutcome.UnknownProvince)]
    [InlineData(null, null, AddressCheckOutcome.Valid)]
    public async Task A_ward_must_belong_to_the_province(int? province, int? ward, AddressCheckOutcome expected)
    {
        var units = AddressTestData.Service(new FakeAdministrativeUnitSource());

        Assert.Equal(expected, (await units.CheckAsync(province, ward, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_valid_check_gives_the_names_of_the_list()
    {
        var units = AddressTestData.Service(new FakeAdministrativeUnitSource());

        var check = await units.CheckAsync(1, 10, CancellationToken.None);

        Assert.Equal(("Thành phố Hà Nội", "Phường Cầu Giấy"), (check.ProvinceName, check.WardName));
    }

    [Fact]
    public async Task The_check_is_skipped_while_the_list_is_down()
    {
        var units = AddressTestData.Service(new FakeAdministrativeUnitSource { Down = true });

        Assert.Equal(AddressCheckOutcome.NotChecked, (await units.CheckAsync(1, 300, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("Hà Nội", "Cầu Giấy", 1, 10)]
    [InlineData("TP. Hà Nội", "Phường Cầu Giấy", 1, 10)]
    [InlineData("Thành phố Hồ Chí Minh", "Phường 01", 79, 301)]
    [InlineData("Tỉnh An Giang", "Đặc khu Phú Quốc", 91, 400)]
    [InlineData("Hà Nội", "Phường Bến Nghé", 1, null)]
    [InlineData("Hà Nội", "Quận Cầu Giấy", 1, null)]
    [InlineData("Hà Nội", null, 1, null)]
    [InlineData("Bà Rịa - Vũng Tàu", "Phường 1", null, null)]
    [InlineData(null, "Phường Cầu Giấy", null, null)]
    public async Task Old_free_text_names_are_matched_to_codes_when_they_match(string? province, string? ward, int? provinceCode, int? wardCode)
    {
        var units = AddressTestData.Service(new FakeAdministrativeUnitSource());

        var match = await units.MatchNamesAsync(province, ward, CancellationToken.None);

        Assert.Equal((provinceCode, wardCode), (match.ProvinceCode, match.WardCode));
    }

    [Fact]
    public async Task Names_are_not_matched_while_the_list_is_down()
    {
        var units = AddressTestData.Service(new FakeAdministrativeUnitSource { Down = true });

        var match = await units.MatchNamesAsync("Hà Nội", "Phường Cầu Giấy", CancellationToken.None);

        Assert.Equal((null, null), (match.ProvinceCode, match.WardCode));
    }
}
