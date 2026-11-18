using OMTS.Application.Features.AdministrativeUnits;
using OMTS.Application.UnitTests.Fakes;

namespace OMTS.Application.UnitTests.Features.AdministrativeUnits;

public sealed class AddressSplitterTests
{
    private static AddressSplit Split(string address)
    {
        var province = AddressSplitter.SplitProvince(address, AddressTestData.Provinces);
        Assert.NotNull(province);
        return AddressSplitter.SplitWard(province, AddressTestData.WardsOf(province.Province.Code));
    }

    [Theory]
    [InlineData("Thành phố Hà Nội", "ha noi")]
    [InlineData("TP Hà Nội", "ha noi")]
    [InlineData("TP. Hồ Chí Minh", "ho chi minh")]
    [InlineData("Tỉnh An Giang", "an giang")]
    [InlineData("  thành   phố   Đà Nẵng ", "da nang")]
    public void A_province_key_has_no_prefix_no_diacritics_and_one_spelling(string name, string key) =>
        Assert.Equal(key, AddressNames.Key(name, AddressLevel.Province));

    [Theory]
    [InlineData("Phường Cầu Giấy", "cau giay")]
    [InlineData("P. Cầu Giấy", "cau giay")]
    [InlineData("Xã Sóc Sơn", "soc son")]
    [InlineData("Đặc khu Phú Quốc", "phu quoc")]
    [InlineData("Thị trấn Sapa", "sapa")]
    [InlineData("Phường 01", "1")]
    [InlineData("P.1", "1")]
    public void A_ward_key_has_no_prefix_no_diacritics_and_no_leading_zero(string name, string key) =>
        Assert.Equal(key, AddressNames.Key(name, AddressLevel.Ward));

    [Fact]
    public void A_prefix_alone_is_a_name_not_a_prefix() =>
        Assert.Equal("phuong", AddressNames.Key("Phường", AddressLevel.Ward));

    [Fact]
    public void The_address_of_the_registry_is_split_into_street_ward_and_province()
    {
        var split = Split("Số 5, Phố Nguyễn Hữu An, Phường Cầu Giấy, TP Hà Nội");

        Assert.Equal(AddressTestData.HaNoi, split.Province);
        Assert.Equal(AddressTestData.CauGiay, split.Ward);
        Assert.Equal("Số 5, Phố Nguyễn Hữu An", split.Street);
    }

    [Fact]
    public void A_closing_country_name_is_ignored()
    {
        var split = Split("12 Lê Lợi, Phường Bến Nghé, Thành phố Hồ Chí Minh, Việt Nam");

        Assert.Equal((79, 300, "12 Lê Lợi"), (split.Province.Code, split.Ward!.Code, split.Street));
    }

    [Fact]
    public void An_old_district_between_the_ward_and_the_province_is_dropped()
    {
        var split = Split("Số 5, Phường Cầu Giấy, Quận Cầu Giấy, Hà Nội");

        Assert.Equal((10, "Số 5"), (split.Ward!.Code, split.Street));
    }

    [Fact]
    public void An_address_with_no_ward_of_the_province_keeps_the_rest_as_the_street()
    {
        var split = Split("Số 5, Phường Không Có, TP Hà Nội");

        Assert.Null(split.Ward);
        Assert.Equal("Số 5, Phường Không Có", split.Street);
    }

    [Fact]
    public void An_address_that_is_only_a_ward_and_a_province_has_no_street()
    {
        var split = Split("Phường Ba Đình, Hà Nội");

        Assert.Equal(AddressTestData.BaDinh, split.Ward);
        Assert.Null(split.Street);
    }

    [Fact]
    public void A_province_that_is_not_in_the_list_splits_nothing()
    {
        Assert.Null(AddressSplitter.SplitProvince("Số 5, Phường Cầu Giấy, Tỉnh Bà Rịa - Vũng Tàu", AddressTestData.Provinces));
        Assert.Null(AddressSplitter.SplitProvince("", AddressTestData.Provinces));
        Assert.Null(AddressSplitter.SplitProvince(null, AddressTestData.Provinces));
    }

    [Fact]
    public void Two_wards_of_one_name_are_told_apart_by_their_prefix()
    {
        Assert.Equal(AddressTestData.SocSon, AddressSplitter.MatchWard("Xã Sóc Sơn", AddressTestData.WardsOf(1)));
        Assert.Equal(AddressTestData.PhuongSocSon, AddressSplitter.MatchWard("Phường Sóc Sơn", AddressTestData.WardsOf(1)));
        // Without a prefix the first of the list is taken.
        Assert.NotNull(AddressSplitter.MatchWard("Sóc Sơn", AddressTestData.WardsOf(1)));
    }

    [Fact]
    public void A_numbered_ward_matches_with_or_without_the_leading_zero_and_the_prefix() =>
        Assert.All(new[] { "Phường 1", "P.01", "phuong 1", "1" }, text => Assert.Equal(AddressTestData.PhuongMot, AddressSplitter.MatchWard(text, AddressTestData.WardsOf(79))));
}
