using OMTS.Application.Features.Cooperatives;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// FARM-01: completes the cooperative profile (tax code, address, contact, business lines) and sets a
/// generated placeholder logo. The full name stays what the bootstrap created. A profile that already has a
/// tax code, or a logo, is not touched.
/// </summary>
internal sealed class DemoCooperativeProfileStep : IDemoSeedStep
{
    public string Name => "cooperative profile";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<CooperativeProfileService>();
        var profile = DemoSeedContext.Ensure(await service.GetAsync(cancellationToken), "reading the cooperative profile");
        var created = 0;

        if (string.IsNullOrEmpty(profile.TaxCode))
        {
            var saved = DemoSeedContext.Ensure(await service.UpdateAsync(new UpdateCooperativeProfileRequest(
                profile.FullName,
                "0201234567",
                "Thôn Cái Rồng, Thị trấn Cái Rồng, Huyện Vân Đồn, Tỉnh Quảng Ninh",
                "0900000010",
                "htx.vandon.demo@example.com",
                "https://htx-vandon.example.com",
                "Nguyễn Văn Hải",
                ["Nuôi hàu Thái Bình Dương thương phẩm", "Sản xuất và cung ứng hàu giống", "Thu mua và phân phối hàu tươi"],
                profile.RowVersion), cancellationToken), "saving the cooperative profile");
            profile = saved.Profile;
            created++;
        }

        if (profile.LogoUrl is null)
        {
            DemoSeedContext.Ensure(await service.UploadLogoAsync(DemoFiles.Logo(), cancellationToken), "uploading the logo");
            created++;
        }

        return created;
    }
}
