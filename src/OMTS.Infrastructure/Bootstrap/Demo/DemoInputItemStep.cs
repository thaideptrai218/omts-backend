using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// PURCHASE-08: fifteen farming input items in the three groups (Seed, Material, Other) with the default codes VT001…VT015
/// (GBR-03); every Seed item uses "con" (BR-PURCHASE-10) and sells as one product ("Hàu béo", "Hàu sữa" or "Hàu sống loại nhỏ"). An item is skipped when its code or its name already exists.
/// </summary>
internal sealed class DemoInputItemStep : IDemoSeedStep
{
    private static readonly (string Code, string Name, InputItemGroup Group, string Unit, string Description, string? Product)[] Items =
    [
        ("VT001", "Hàu giống Thái Bình Dương 3 cm", InputItemGroup.Seed, "con", "Hàu giống cỡ 3 cm, giao theo lô", "Hàu béo"),
        ("VT002", "Hàu giống Thái Bình Dương 5 cm", InputItemGroup.Seed, "con", "Hàu giống cỡ 5 cm", "Hàu béo"),
        ("VT003", "Hàu giống bản địa Vân Đồn 3 cm", InputItemGroup.Seed, "con", "Giống hàu cửa sông bản địa", "Hàu sữa"),
        ("VT004", "Hàu giống Cổ Trai 4 cm", InputItemGroup.Seed, "con", "Hàu giống cỡ 4 cm", "Hàu sống loại nhỏ"),
        ("VT005", "Phao nhựa HDPE 200 lít", InputItemGroup.Material, "cái", "Phao nổi cho bè", null),
        ("VT006", "Dây thừng nilon 14 mm", InputItemGroup.Material, "cuộn", "Cuộn 200 m", null),
        ("VT007", "Lưới nhựa bao hàu", InputItemGroup.Material, "m", "Khổ rộng 1,5 m", null),
        ("VT008", "Thanh gỗ lim khung bè", InputItemGroup.Material, "cái", "Dài 6 m", null),
        ("VT009", "Cọc neo bê tông", InputItemGroup.Material, "cái", "Cọc neo 200 kg", null),
        ("VT010", "Dây buộc lồng hàu", InputItemGroup.Material, "kg", "Dây nilon buộc lồng", null),
        ("VT011", "Dầu diesel", InputItemGroup.Other, "lít", "Nhiên liệu cho tàu thuyền ra bè", null),
        ("VT012", "Găng tay bảo hộ", InputItemGroup.Other, "đôi", "Găng cao su chống cắt", null),
        ("VT013", "Áo phao cứu sinh", InputItemGroup.Other, "cái", "Áo phao người lớn", null),
        ("VT014", "Vôi sát khuẩn", InputItemGroup.Other, "kg", "Xử lý khu vực sơ chế", null),
        ("VT015", "Thùng xốp vận chuyển", InputItemGroup.Other, "cái", "Thùng 30 lít", null),
    ];

    public string Name => "farming input items";

    /// <summary>The name of a demo item: a database seeded before the VT codes knows it by this name only.</summary>
    public static string NameOf(string code) => Items.Single(i => i.Code == code).Name;

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<FarmingInputItemService>();
        var products = await context.Db.Products.Where(p => p.StatusId == StatusIds.Usage.InUse).ToDictionaryAsync(p => p.Name, p => p.Id, cancellationToken);
        var created = 0;
        foreach (var (code, name, group, unit, description, product) in Items)
        {
            // A database seeded before the VT codes keeps its items: the same name may not be created twice.
            var existing = await context.Db.FarmingInputItems.FirstOrDefaultAsync(i => i.Code == code || i.Name == name, cancellationToken);
            if (existing is not null)
            {
                continue;
            }

            Guid? productId = product is not null && products.TryGetValue(product, out var found) ? found : null;
            DemoSeedContext.Ensure(
                await service.CreateAsync(new SaveFarmingInputItemRequest(code, name, group, unit, description, ProductId: productId), cancellationToken), $"item {code}");
            created++;
        }

        return created + await MapSeedsToProductsAsync(context, products, cancellationToken);
    }

    /// <summary>
    /// The product each demo seed item sells as (owner decision of 13/11/2026: a cycle farms the product of the seed of its batch). A seed item of an older database that has no product yet gets it; one that has
    /// (set by the Owner, or by the line above) is not touched, so a rerun changes nothing and the Owner's choice is kept.
    /// </summary>
    private static async Task<int> MapSeedsToProductsAsync(DemoSeedContext context, IReadOnlyDictionary<string, Guid> products, CancellationToken cancellationToken)
    {
        var mapped = 0;
        foreach (var (code, name, group, _, _, product) in Items.Where(i => i.Group == InputItemGroup.Seed && i.Product is not null))
        {
            var item = await context.Db.FarmingInputItems.FirstOrDefaultAsync(i => (i.Code == code || i.Name == name) && i.Group == group && i.ProductId == null, cancellationToken);
            if (item is null || !products.TryGetValue(product!, out var productId))
            {
                continue;
            }

            item.ProductId = productId;
            mapped++;
        }

        if (mapped > 0)
        {
            await context.Db.SaveChangesAsync(cancellationToken);
        }

        return mapped;
    }
}
