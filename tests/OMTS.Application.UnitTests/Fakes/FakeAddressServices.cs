using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Interfaces.Addresses;
using OMTS.Application.Features.AdministrativeUnits;

namespace OMTS.Application.UnitTests.Fakes;

/// <summary>A few provinces and wards of the two-level list, written as the outside list writes them.</summary>
public static class AddressTestData
{
    public static readonly ProvinceUnit HaNoi = new(1, "Thành phố Hà Nội", "thành phố trung ương");
    public static readonly ProvinceUnit Hue = new(46, "Thành phố Huế", "thành phố trung ương");
    public static readonly ProvinceUnit DaNang = new(48, "Thành phố Đà Nẵng", "thành phố trung ương");
    public static readonly ProvinceUnit HoChiMinh = new(79, "Thành phố Hồ Chí Minh", "thành phố trung ương");
    public static readonly ProvinceUnit AnGiang = new(91, "Tỉnh An Giang", "tỉnh");

    public static readonly WardUnit CauGiay = new(10, "Phường Cầu Giấy", "phường", 1);
    public static readonly WardUnit BaDinh = new(4, "Phường Ba Đình", "phường", 1);
    public static readonly WardUnit SocSon = new(200, "Xã Sóc Sơn", "xã", 1);
    public static readonly WardUnit PhuongSocSon = new(201, "Phường Sóc Sơn", "phường", 1);
    public static readonly WardUnit BenNghe = new(300, "Phường Bến Nghé", "phường", 79);
    public static readonly WardUnit PhuongMot = new(301, "Phường 1", "phường", 79);
    public static readonly WardUnit PhuQuoc = new(400, "Đặc khu Phú Quốc", "đặc khu", 91);

    public static IReadOnlyList<ProvinceUnit> Provinces => [HoChiMinh, HaNoi, AnGiang, DaNang, Hue];

    public static IReadOnlyList<WardUnit> WardsOf(int provinceCode) => provinceCode switch
    {
        1 => [CauGiay, BaDinh, SocSon, PhuongSocSon],
        79 => [BenNghe, PhuongMot],
        91 => [PhuQuoc],
        _ => [],
    };

    public static AdministrativeUnitService Service(IAdministrativeUnitSource source, IMemoryCache? cache = null) =>
        new(source, cache ?? new MemoryCache(new MemoryCacheOptions()));
}

/// <summary>The outside list: counts the calls, and answers null (not responding) while <see cref="Down"/>.</summary>
public sealed class FakeAdministrativeUnitSource : IAdministrativeUnitSource
{
    public bool Down { get; set; }
    public int ProvinceCalls { get; private set; }
    public int WardCalls { get; private set; }

    public Task<IReadOnlyList<ProvinceUnit>?> GetProvincesAsync(CancellationToken cancellationToken)
    {
        ProvinceCalls++;
        return Task.FromResult(Down ? null : (IReadOnlyList<ProvinceUnit>?)AddressTestData.Provinces);
    }

    public Task<IReadOnlyList<WardUnit>?> GetWardsAsync(int provinceCode, CancellationToken cancellationToken)
    {
        WardCalls++;
        return Task.FromResult(Down ? null : (IReadOnlyList<WardUnit>?)AddressTestData.WardsOf(provinceCode));
    }
}

/// <summary>The tax registry: one company by tax code, counts the calls.</summary>
public sealed class FakeTaxRegistry : ITaxRegistry
{
    public const string CompanyTaxCode = "0100109106";

    public TaxRegistryOutcome Outcome { get; set; } = TaxRegistryOutcome.Found;
    public string Address { get; set; } = "Số 5, Phố Nguyễn Hữu An, Phường Cầu Giấy, TP Hà Nội";
    public int Calls { get; private set; }

    public Task<TaxRegistryLookup> FindAsync(string taxCode, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Outcome == TaxRegistryOutcome.Found
            ? new TaxRegistryLookup(TaxRegistryOutcome.Found, new TaxRegistryCompany(taxCode, "TẬP ĐOÀN CÔNG NGHIỆP - VIỄN THÔNG QUÂN ĐỘI", null, Address, "NNT đang hoạt động"))
            : new TaxRegistryLookup(Outcome));
    }
}
