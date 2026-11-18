using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OMTS.Application.Common.Interfaces.Addresses;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>A small administrative units list in place of the outside one, so no test reaches the network. Hà Nội (1) has Ba Đình (4) and Cầu Giấy (10); Hồ Chí Minh (79) has Bến Nghé (300).</summary>
public sealed class StubAdministrativeUnitSource : IAdministrativeUnitSource
{
    private static readonly ProvinceUnit[] Provinces = [new(1, "Thành phố Hà Nội", "thành phố trung ương"), new(79, "Thành phố Hồ Chí Minh", "thành phố trung ương")];

    private static readonly WardUnit[] Wards =
    [
        new(4, "Phường Ba Đình", "phường", 1), new(10, "Phường Cầu Giấy", "phường", 1), new(300, "Phường Bến Nghé", "phường", 79),
    ];

    public Task<IReadOnlyList<ProvinceUnit>?> GetProvincesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProvinceUnit>?>(Provinces);

    public Task<IReadOnlyList<WardUnit>?> GetWardsAsync(int provinceCode, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WardUnit>?>(Wards.Where(w => w.ProvinceCode == provinceCode).ToList());
}

/// <summary>The tax registry: 0100109106 is a company with a Hà Nội address, 0000000001 is not found, 0000000002 does not answer.</summary>
public sealed class StubTaxRegistry : ITaxRegistry
{
    public Task<TaxRegistryLookup> FindAsync(string taxCode, CancellationToken cancellationToken) => Task.FromResult(taxCode switch
    {
        "0100109106" => new TaxRegistryLookup(
            TaxRegistryOutcome.Found,
            new TaxRegistryCompany(taxCode, "TẬP ĐOÀN CÔNG NGHIỆP - VIỄN THÔNG QUÂN ĐỘI", null, "Số 5, Phố Nguyễn Hữu An, Phường Cầu Giấy, TP Hà Nội", "NNT đang hoạt động")),
        "0000000002" => TaxRegistryLookup.Unavailable,
        _ => TaxRegistryLookup.NotFound,
    });
}

public static class StubAddressServicesExtensions
{
    /// <summary>Replaces the two outside services of the address and invoice forms.</summary>
    public static IServiceCollection UseStubAddressServices(this IServiceCollection services)
    {
        services.RemoveAll<IAdministrativeUnitSource>();
        services.RemoveAll<ITaxRegistry>();
        services.AddSingleton<IAdministrativeUnitSource, StubAdministrativeUnitSource>();
        services.AddSingleton<ITaxRegistry, StubTaxRegistry>();
        return services;
    }
}
