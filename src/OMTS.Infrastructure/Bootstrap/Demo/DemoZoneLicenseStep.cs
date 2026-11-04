using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// FARM-03: licenses of the In Use demo zones, one zone each, dated from today and matched by number. DEMO-01: a license
/// that expired 400 days ago, replaced by the current one, which expires in 20 days (the daily reminder sends MSG-FARM-01
/// for it). DEMO-02: the current license, valid for 700 more days, and an older food safety commitment that expired
/// 15 days ago (the daily reminder sends MSG-FARM-02 for it: every license that is not Replaced is reminded, GFR-03).
/// </summary>
internal sealed class DemoZoneLicenseStep : IDemoSeedStep
{
    private sealed record DemoLicense(
        string ZoneCode, string Type, string Number, int EffectiveDays, int ExpiryDays, string Authority, string? Replaces = null);

    private const string Province = "UBND tỉnh Quảng Ninh";
    private const string Agriculture = "Sở Nông nghiệp và Phát triển nông thôn Quảng Ninh";

    // In order: a replaced license comes before the one replacing it.
    private static readonly DemoLicense[] Licenses =
    [
        new(DemoFarmingZoneStep.ZoneOne, LicenseTypes.AquacultureLicense, "NTTS-2023-DEMO-01", -1100, -400, Agriculture),
        new(DemoFarmingZoneStep.ZoneOne, LicenseTypes.AquacultureLicense, "NTTS-2025-DEMO-01", -400, 20, Agriculture, Replaces: "NTTS-2023-DEMO-01"),
        new(DemoFarmingZoneStep.ZoneTwo, LicenseTypes.FoodSafetyCommitment, "ATTP-DEMO-02", -800, -15, "Chi cục Thủy sản Quảng Ninh"),
        new(DemoFarmingZoneStep.ZoneTwo, LicenseTypes.SeaAreaAllocation, "GKV-2025-DEMO-02", -300, 700, Province),
    ];

    public string Name => "zone licenses";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<ZoneLicenseService>();
        var created = 0;
        foreach (var license in Licenses)
        {
            if (await context.Db.ZoneComplianceDocuments.AnyAsync(d => d.DocumentNumber == license.Number, cancellationToken))
            {
                continue;
            }

            var zoneId = await context.Db.FarmingZones.Where(z => z.Code == license.ZoneCode).Select(z => z.Id).SingleAsync(cancellationToken);
            var request = new SaveZoneLicenseRequest
            {
                DocumentType = license.Type,
                DocumentNumber = license.Number,
                EffectiveDate = context.Today.AddDays(license.EffectiveDays),
                ExpiryDate = context.Today.AddDays(license.ExpiryDays),
                IssuingAuthority = license.Authority,
                Holder = "Hợp tác xã Nuôi hàu Vân Đồn",
                LegalScope = $"Nuôi hàu bè tại {license.ZoneCode}, Vân Đồn, Quảng Ninh",
                ZoneId = zoneId,
            };
            var file = DemoFiles.LicenseDocument(license.Number);
            if (license.Replaces is { } replacedNumber)
            {
                var replacedId = await context.Db.ZoneComplianceDocuments.Where(d => d.DocumentNumber == replacedNumber).Select(d => d.Id).SingleAsync(cancellationToken);
                DemoSeedContext.Ensure(await service.ReplaceAsync(replacedId, request, file, cancellationToken), $"license {license.Number}");
            }
            else
            {
                DemoSeedContext.Ensure(await service.CreateAsync(request, file, cancellationToken), $"license {license.Number}");
            }

            created++;
        }

        return created;
    }
}
