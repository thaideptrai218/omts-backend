using OMTS.Application.Features.Purchasing.Suppliers;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>PURCHASE-07: five suppliers (phones 0900 000 031…035) of the three types, matched by phone number.</summary>
internal sealed class DemoSupplierStep : IDemoSeedStep
{
    public const string SeedHatcheryPhone = "0900000031";
    public const string SeedFarmPhone = "0900000032";
    public const string MaterialCompanyPhone = "0900000033";
    public const string MaterialShopPhone = "0900000034";
    public const string GeneralStorePhone = "0900000035";

    // Fake addresses and emails (example.com) only.
    private static readonly (string Name, SupplierType Type, string Phone, string Address, string? Email)[] Suppliers =
    [
        ("Trại giống hàu Cái Rồng", SupplierType.Seed, SeedHatcheryPhone, "Thị trấn Cái Rồng, Vân Đồn, Quảng Ninh", "giongcairong@example.com"),
        ("Cơ sở giống thủy sản Bạch Long", SupplierType.Seed, SeedFarmPhone, "Cát Hải, Hải Phòng", null),
        ("Công ty TNHH Vật tư nuôi biển Hạ Long", SupplierType.Material, MaterialCompanyPhone, "Phường Cao Thắng, TP Hạ Long, Quảng Ninh", "vattuhalong@example.com"),
        ("Cửa hàng phao bè Vân Đồn", SupplierType.Material, MaterialShopPhone, "Thị trấn Cái Rồng, Vân Đồn, Quảng Ninh", null),
        ("Đại lý vật tư tổng hợp Quan Lạn", SupplierType.Other, GeneralStorePhone, "Bến Quan Lạn, Vân Đồn, Quảng Ninh", null),
    ];

    public string Name => "suppliers";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<SupplierService>();
        var created = 0;
        foreach (var (name, type, phone, address, email) in Suppliers)
        {
            var normalized = PhoneNumbers.Normalize(phone);
            if (await context.Db.Suppliers.AnyAsync(s => s.PhoneNumber == normalized, cancellationToken))
            {
                continue;
            }

            DemoSeedContext.Ensure(await service.CreateAsync(new SaveSupplierRequest(name, type, phone, address, email), cancellationToken), $"supplier {name}");
            created++;
        }

        return created;
    }
}
