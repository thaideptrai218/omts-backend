using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// PURCHASE-08: fifteen farming input items in the three groups (Seed, Material, Other) with the default codes VT001…VT015
/// (GBR-03); every Seed item uses "con" (BR-PURCHASE-10). An item is skipped when its code or its name already exists.
/// </summary>
internal sealed class DemoInputItemStep : IDemoSeedStep
{
    private static readonly (string Code, string Name, InputItemGroup Group, string Unit, string Description)[] Items =
    [
        ("VT001", "Hàu giống Thái Bình Dương 3 cm", InputItemGroup.Seed, "con", "Hàu giống cỡ 3 cm, giao theo lô"),
        ("VT002", "Hàu giống Thái Bình Dương 5 cm", InputItemGroup.Seed, "con", "Hàu giống cỡ 5 cm"),
        ("VT003", "Hàu giống bản địa Vân Đồn 3 cm", InputItemGroup.Seed, "con", "Giống hàu cửa sông bản địa"),
        ("VT004", "Hàu giống Cổ Trai 4 cm", InputItemGroup.Seed, "con", "Hàu giống cỡ 4 cm"),
        ("VT005", "Phao nhựa HDPE 200 lít", InputItemGroup.Material, "cái", "Phao nổi cho bè"),
        ("VT006", "Dây thừng nilon 14 mm", InputItemGroup.Material, "cuộn", "Cuộn 200 m"),
        ("VT007", "Lưới nhựa bao hàu", InputItemGroup.Material, "m", "Khổ rộng 1,5 m"),
        ("VT008", "Thanh gỗ lim khung bè", InputItemGroup.Material, "cái", "Dài 6 m"),
        ("VT009", "Cọc neo bê tông", InputItemGroup.Material, "cái", "Cọc neo 200 kg"),
        ("VT010", "Dây buộc lồng hàu", InputItemGroup.Material, "kg", "Dây nilon buộc lồng"),
        ("VT011", "Dầu diesel", InputItemGroup.Other, "lít", "Nhiên liệu cho tàu thuyền ra bè"),
        ("VT012", "Găng tay bảo hộ", InputItemGroup.Other, "đôi", "Găng cao su chống cắt"),
        ("VT013", "Áo phao cứu sinh", InputItemGroup.Other, "cái", "Áo phao người lớn"),
        ("VT014", "Vôi sát khuẩn", InputItemGroup.Other, "kg", "Xử lý khu vực sơ chế"),
        ("VT015", "Thùng xốp vận chuyển", InputItemGroup.Other, "cái", "Thùng 30 lít"),
    ];

    public string Name => "farming input items";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<FarmingInputItemService>();
        var created = 0;
        foreach (var (code, name, group, unit, description) in Items)
        {
            // A database seeded before the VT codes keeps its items: the same name may not be created twice.
            if (await context.Db.FarmingInputItems.AnyAsync(i => i.Code == code || i.Name == name, cancellationToken))
            {
                continue;
            }

            DemoSeedContext.Ensure(
                await service.CreateAsync(new SaveFarmingInputItemRequest(code, name, group, unit, description), cancellationToken), $"item {code}");
            created++;
        }

        return created;
    }
}
