using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Cooperatives;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// FARM-01: completes the cooperative profile (short name, tax code, year established, address, contacts, social link,
/// introduction in both languages), sets a generated placeholder logo and cover photo, and adds one Valid certification.
/// The full name stays what the bootstrap created. A profile that already has a tax code and a short name, a logo, a
/// cover photo or the demo certification is not touched; a profile seeded before the short name existed keeps its values
/// and only gets the new fields.
/// </summary>
internal sealed class DemoCooperativeProfileStep : IDemoSeedStep
{
    private const string CertificationNumber = "VG-DEMO-2026-01";

    public string Name => "cooperative profile";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<CooperativeProfileService>();
        var profile = DemoSeedContext.Ensure(await service.GetAsync(cancellationToken), "reading the cooperative profile");
        var created = 0;

        if (string.IsNullOrEmpty(profile.TaxCode) || string.IsNullOrEmpty(profile.ShortName))
        {
            var saved = DemoSeedContext.Ensure(await service.UpdateAsync(new UpdateCooperativeProfileRequest
            {
                FullName = profile.FullName,
                ShortName = profile.ShortName ?? "HTX Hàu Vân Đồn",
                TaxCode = profile.TaxCode ?? "0201234567",
                YearEstablished = profile.YearEstablished ?? 2012,
                OfficeAddress = profile.OfficeAddress ?? "Thôn Cái Rồng, Thị trấn Cái Rồng, Huyện Vân Đồn, Tỉnh Quảng Ninh",
                PhoneNumber = profile.PhoneNumber ?? "0900000010",
                Email = profile.Email ?? "htx.vandon.demo@example.com",
                Website = profile.Website ?? "https://htx-vandon.example.com",
                SocialLink = profile.SocialLink ?? "https://social.example.com/htx-vandon",
                IntroductionVi = profile.IntroductionVi ??
                    "Hợp tác xã Nuôi hàu Vân Đồn nuôi hàu Thái Bình Dương trên bè tại vùng biển Vân Đồn, Quảng Ninh, từ năm 2012. " +
                    "Hàu được nuôi từ con giống chọn lọc, nước biển được đo mỗi tháng và mỗi lô thu hoạch đều truy được về bè nuôi.",
                IntroductionEn = profile.IntroductionEn ??
                    "Van Don Oyster Cooperative has farmed Pacific oysters on rafts in the sea of Van Don, Quang Ninh, since 2012. " +
                    "The oysters grow from selected seed, the sea water is measured every month and every harvest lot is traced to its raft.",
                LegalRepresentative = profile.LegalRepresentative ?? "Nguyễn Văn Hải",
                BusinessLines = profile.BusinessLines.Count > 0
                    ? profile.BusinessLines
                    : ["Nuôi hàu Thái Bình Dương thương phẩm", "Sản xuất và cung ứng hàu giống", "Thu mua và phân phối hàu tươi"],
                RowVersion = profile.RowVersion,
            }, cancellationToken), "saving the cooperative profile");
            profile = saved.Profile;
            created++;
        }

        if (profile.LogoUrl is null)
        {
            DemoSeedContext.Ensure(await service.UploadLogoAsync(DemoFiles.Logo(), cancellationToken), "uploading the logo");
            created++;
        }

        if (profile.CoverPhotoUrl is null)
        {
            DemoSeedContext.Ensure(
                await context.Get<CooperativeCoverPhotoService>().UploadAsync(DemoFiles.CoverPhoto(), cancellationToken), "uploading the cover photo");
            created++;
        }

        if (!await context.Db.CooperativeCertifications.AnyAsync(c => c.Number == CertificationNumber, cancellationToken))
        {
            DemoSeedContext.Ensure(await context.Get<CooperativeCertificationService>().CreateAsync(
                new SaveCooperativeCertificationRequest
                {
                    Name = "Chứng nhận VietGAP nuôi hàu",
                    Number = CertificationNumber,
                    Issuer = "Tổ chức chứng nhận VietGAP (dữ liệu mẫu)",
                    ExpiryDate = context.Today.AddDays(500),
                },
                DemoFiles.LicenseDocument(CertificationNumber),
                cancellationToken), $"certification {CertificationNumber}");
            created++;
        }

        return created;
    }
}
