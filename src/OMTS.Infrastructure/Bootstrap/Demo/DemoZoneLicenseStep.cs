using OMTS.Application.Features.Farming.Licenses.Dtos;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// FARM-03: three license documents for each In Use demo zone, dated from today: one valid, one expiring in
/// 20 days (the daily reminder picks it up) and one expired 15 days ago. Matched by document number.
/// </summary>
internal sealed class DemoZoneLicenseStep : IDemoSeedStep
{
    // (type, number suffix, effective days from today, expiry days from today, issuing authority)
    private static readonly (string Type, string Suffix, int EffectiveDays, int ExpiryDays, string Authority)[] Licenses =
    [
        (LicenseTypes.SeaAreaAllocation, "GKV", -400, 700, "UBND tỉnh Quảng Ninh"),
        (LicenseTypes.AquacultureLicense, "NTTS", -700, 20, "Sở Nông nghiệp và Phát triển nông thôn Quảng Ninh"),
        (LicenseTypes.FoodSafetyCommitment, "ATTP", -380, -15, "Chi cục Thủy sản Quảng Ninh"),
    ];

    public string Name => "zone licenses";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<ZoneLicenseService>();
        var created = 0;
        foreach (var zoneCode in new[] { DemoFarmingZoneStep.ZoneOne, DemoFarmingZoneStep.ZoneTwo })
        {
            var zoneId = await context.Db.FarmingZones.Where(z => z.Code == zoneCode).Select(z => z.Id).SingleAsync(cancellationToken);
            foreach (var license in Licenses)
            {
                var number = $"{license.Suffix}-{zoneCode}";
                if (await context.Db.ZoneComplianceDocuments.AnyAsync(d => d.DocumentNumber == number, cancellationToken))
                {
                    continue;
                }

                DemoSeedContext.Ensure(await service.CreateAsync(
                    new SaveZoneLicenseRequest
                    {
                        DocumentType = license.Type,
                        DocumentNumber = number,
                        EffectiveDate = context.Today.AddDays(license.EffectiveDays),
                        ExpiryDate = context.Today.AddDays(license.ExpiryDays),
                        IssuingAuthority = license.Authority,
                        Holder = "Hợp tác xã Nuôi hàu Vân Đồn",
                        LegalScope = $"Nuôi hàu bè tại {zoneCode}, Vân Đồn, Quảng Ninh",
                        ZoneIds = [zoneId],
                    },
                    DemoFiles.LicenseDocument(number),
                    cancellationToken), $"license {number}");
                created++;
            }
        }

        return created;
    }
}
