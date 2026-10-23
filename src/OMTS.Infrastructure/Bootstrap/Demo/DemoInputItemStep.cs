using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>INP-02: fifteen farming input items in the three groups (seed, raft material, other), matched by item code.</summary>
internal sealed class DemoInputItemStep : IDemoSeedStep
{
    private static readonly (string Code, string Name, InputItemGroup Group, string Unit, string Description)[] Items =
    [
        ("SEED-01", "Hàu giống Thái Bình Dương 3 cm", InputItemGroup.Seed, "con", "Hàu giống cỡ 3 cm, giao theo lô"),
        ("SEED-02", "Hàu giống Thái Bình Dương 5 cm", InputItemGroup.Seed, "con", "Hàu giống cỡ 5 cm"),
        ("SEED-03", "Hàu giống bản địa Vân Đồn 3 cm", InputItemGroup.Seed, "con", "Giống hàu cửa sông bản địa"),
        ("SEED-04", "Hàu giống Cổ Trai 4 cm", InputItemGroup.Seed, "con", "Hàu giống cỡ 4 cm"),
        ("MAT-01", "Phao nhựa HDPE 200 lít", InputItemGroup.RaftMaterial, "cái", "Phao nổi cho bè"),
        ("MAT-02", "Dây thừng nilon 14 mm", InputItemGroup.RaftMaterial, "cuộn", "Cuộn 200 m"),
        ("MAT-03", "Lưới nhựa bao hàu", InputItemGroup.RaftMaterial, "m", "Khổ rộng 1,5 m"),
        ("MAT-04", "Thanh gỗ lim khung bè", InputItemGroup.RaftMaterial, "cái", "Dài 6 m"),
        ("MAT-05", "Cọc neo bê tông", InputItemGroup.RaftMaterial, "cái", "Cọc neo 200 kg"),
        ("MAT-06", "Dây buộc lồng hàu", InputItemGroup.RaftMaterial, "kg", "Dây nilon buộc lồng"),
        ("OTH-01", "Dầu diesel", InputItemGroup.Other, "lít", "Nhiên liệu cho tàu thuyền ra bè"),
        ("OTH-02", "Găng tay bảo hộ", InputItemGroup.Other, "đôi", "Găng cao su chống cắt"),
        ("OTH-03", "Áo phao cứu sinh", InputItemGroup.Other, "cái", "Áo phao người lớn"),
        ("OTH-04", "Vôi sát khuẩn", InputItemGroup.Other, "kg", "Xử lý khu vực sơ chế"),
        ("OTH-05", "Thùng xốp vận chuyển", InputItemGroup.Other, "cái", "Thùng 30 lít"),
    ];

    public string Name => "farming input items";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<FarmingInputItemService>();
        var created = 0;
        foreach (var (code, name, group, unit, description) in Items)
        {
            if (await context.Db.FarmingInputItems.AnyAsync(i => i.Code == code, cancellationToken))
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
