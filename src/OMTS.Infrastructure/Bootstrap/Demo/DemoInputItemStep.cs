using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// PURCHASE-08: fifteen farming input items in the three groups (Seed, Material, Other) with the default codes VT001…VT015
/// (GBR-03); every Seed item uses "mảnh" (BR-PURCHASE-10) and sells as one product ("Hàu Thái Bình Dương" or "Hàu bản địa Vân Đồn"). An item is skipped when its code or its name already exists.
/// </summary>
internal sealed class DemoInputItemStep : IDemoSeedStep
{
    // MaturityDays (seed items only): the Pacific oyster ("hàu sữa") is harvested 7-8 months after seeding at Van Don (240 days; nonghoc.com, baoquangninh.vn); 365 days for the native estuary oyster is an ASSUMPTION for the Owner to correct.
    // The seed size (mm) and the origin are in Notes, not in the name (the bracket tells items of one product apart): the size sold is the commercial size of the product.
    private static readonly (string Code, string Name, InputItemGroup Group, string Unit, string Description, string? Product, int? MaturityDays, string? Notes)[] Items =
    [
        ("VT001", "Giống hàu Thái Bình Dương (sản xuất trong nước)", InputItemGroup.Seed, "mảnh", "Giống hàu sữa sản xuất nhân tạo tại trại giống trong nước", "Hàu Thái Bình Dương", 240,
            "Nguồn: trại giống trong nước. Cỡ giống 3–5 mm (hàu bám vỏ nhỏ). Mùa vụ chính tháng 3–5."),
        ("VT002", "Giống hàu Thái Bình Dương (nhập khẩu)", InputItemGroup.Seed, "mảnh", "Giống hàu sữa nhập khẩu từ trại giống nước ngoài", "Hàu Thái Bình Dương", 240,
            "Nguồn: nhập khẩu. Cỡ giống 3–5 mm. Có giấy kiểm dịch theo lô."),
        ("VT003", "Giống hàu Thái Bình Dương (vụ giống tháng 8–10)", InputItemGroup.Seed, "mảnh", "Giống hàu sữa của vụ sinh sản phụ cuối năm", "Hàu Thái Bình Dương", 240,
            "Nguồn: trại giống trong nước, vụ phụ tháng 8–10 (vụ chính là tháng 3–5). Cỡ giống 3–5 mm."),
        // ASSUMPTION (Owner to confirm): 365 days to maturity and the 3-5 mm seed size of the native oyster have no source; the only source says the native estuary oyster is much smaller and slower than the Pacific oyster.
        ("VT004", "Giống hàu bản địa Vân Đồn", InputItemGroup.Seed, "mảnh", "Giống hàu cửa sông bản địa", "Hàu bản địa Vân Đồn", 365,
            "Nguồn: hàu cửa sông bản địa lấy tại Vân Đồn. Cỡ giống khoảng 3–5 mm (giả định)."),
        ("VT005", "Phao nhựa HDPE 200 lít", InputItemGroup.Material, "cái", "Phao nổi cho bè", null, null, null),
        ("VT006", "Dây thừng nilon 14 mm", InputItemGroup.Material, "cuộn", "Cuộn 200 m", null, null, null),
        ("VT007", "Lưới nhựa bao hàu", InputItemGroup.Material, "m", "Khổ rộng 1,5 m", null, null, null),
        ("VT008", "Thanh gỗ lim khung bè", InputItemGroup.Material, "cái", "Dài 6 m", null, null, null),
        ("VT009", "Cọc neo bê tông", InputItemGroup.Material, "cái", "Cọc neo 200 kg", null, null, null),
        ("VT010", "Dây buộc lồng hàu", InputItemGroup.Material, "kg", "Dây nilon buộc lồng", null, null, null),
        ("VT011", "Dầu diesel", InputItemGroup.Other, "lít", "Nhiên liệu cho tàu thuyền ra bè", null, null, null),
        ("VT012", "Găng tay bảo hộ", InputItemGroup.Other, "đôi", "Găng cao su chống cắt", null, null, null),
        ("VT013", "Áo phao cứu sinh", InputItemGroup.Other, "cái", "Áo phao người lớn", null, null, null),
        ("VT014", "Vôi sát khuẩn", InputItemGroup.Other, "kg", "Xử lý khu vực sơ chế", null, null, null),
        ("VT015", "Thùng xốp vận chuyển", InputItemGroup.Other, "cái", "Thùng 30 lít", null, null, null),
    ];

    public string Name => "farming input items";

    /// <summary>The name of a demo item: a database seeded before the VT codes knows it by this name only.</summary>
    public static string NameOf(string code) => Items.Single(i => i.Code == code).Name;

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<FarmingInputItemService>();
        var products = await context.Db.Products.Where(p => p.StatusId == StatusIds.Usage.InUse).ToDictionaryAsync(p => p.Name, p => p.Id, cancellationToken);
        var created = 0;
        foreach (var (code, name, group, unit, description, product, maturityDays, notes) in Items)
        {
            // A database seeded before the VT codes keeps its items: the same name may not be created twice.
            var existing = await context.Db.FarmingInputItems.FirstOrDefaultAsync(i => i.Code == code || i.Name == name, cancellationToken);
            if (existing is not null)
            {
                continue;
            }

            Guid? productId = product is not null && products.TryGetValue(product, out var found) ? found : null;
            DemoSeedContext.Ensure(
                await service.CreateAsync(new SaveFarmingInputItemRequest(code, name, group, unit, description, Notes: notes, ProductId: productId, MaturityDays: maturityDays), cancellationToken), $"item {code}");
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
        foreach (var (code, name, group, _, _, product, maturityDays, _) in Items.Where(i => i.Group == InputItemGroup.Seed && i.Product is not null))
        {
            var item = await context.Db.FarmingInputItems.FirstOrDefaultAsync(
                i => (i.Code == code || i.Name == name) && i.Group == group && (i.ProductId == null || i.MaturityDays == null), cancellationToken);
            if (item is null)
            {
                continue;
            }

            if (item.ProductId is null && products.TryGetValue(product!, out var productId))
            {
                item.ProductId = productId;
                mapped++;
            }

            // The days to maturity of a seed of an older database; a value the Owner set is kept.
            if (item.MaturityDays is null && maturityDays is { } days)
            {
                item.MaturityDays = days;
                mapped++;
            }
        }

        if (mapped > 0)
        {
            await context.Db.SaveChangesAsync(cancellationToken);
        }

        return mapped;
    }
}
