using OMTS.Application.Features.Purchasing.Suppliers;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>INP-01: five suppliers (phones 0900 000 031…035), matched by phone number.</summary>
internal sealed class DemoSupplierStep : IDemoSeedStep
{
    public const string SeedHatcheryPhone = "0900000031";
    public const string SeedFarmPhone = "0900000032";
    public const string MaterialCompanyPhone = "0900000033";
    public const string MaterialShopPhone = "0900000034";
    public const string GeneralStorePhone = "0900000035";

    private static readonly (string Name, SupplierType Type, string Phone, string Contacts)[] Suppliers =
    [
        ("Trại giống hàu Cái Rồng", SupplierType.Seed, SeedHatcheryPhone, "Anh Bình, Zalo cùng số điện thoại"),
        ("Cơ sở giống thủy sản Bạch Long", SupplierType.Seed, SeedFarmPhone, "Chị Thu, địa chỉ: Cát Hải, Hải Phòng"),
        ("Công ty TNHH Vật tư nuôi biển Hạ Long", SupplierType.Material, MaterialCompanyPhone, "Kho: Phường Cao Thắng, TP Hạ Long"),
        ("Cửa hàng phao bè Vân Đồn", SupplierType.Material, MaterialShopPhone, "Anh Đạt, Thị trấn Cái Rồng"),
        ("Đại lý vật tư tổng hợp Quan Lạn", SupplierType.Other, GeneralStorePhone, "Giao hàng tận bến Quan Lạn"),
    ];

    public string Name => "suppliers";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<SupplierService>();
        var created = 0;
        foreach (var (name, type, phone, contacts) in Suppliers)
        {
            var normalized = PhoneNumbers.Normalize(phone);
            if (await context.Db.Suppliers.AnyAsync(s => s.PhoneNumber == normalized, cancellationToken))
            {
                continue;
            }

            DemoSeedContext.Ensure(await service.CreateAsync(new SaveSupplierRequest(name, type, phone, contacts), cancellationToken), $"supplier {name}");
            created++;
        }

        return created;
    }
}
