using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>
/// The product a cycle will yield and sell (owner decision of 13/11/2026, simplified the same day): it is not chosen but DERIVED from the nursed batch. The seed item of the batch's seed lot carries the product it sells
/// as ("loại hàu bán ra", set once in the catalogue), so the cycle takes it when it is started and whenever its batch is changed (only before the first seeding record). The harvest lot's purchase request must match it.
/// </summary>
public sealed partial class CultivationCycleService
{
    /// <summary>The field a product-less seed item is reported on: the nursery batch the user picked.</summary>
    private const string BatchProductField = "NurseryBatchId";

    /// <summary>Reason code of a batch whose seed item sells as no product yet (400 on <c>NurseryBatchId</c>).</summary>
    public const string SeedItemWithoutProductReason = "SEED_ITEM_WITHOUT_PRODUCT";

    /// <summary>
    /// The product of the batch: that of its seed item. A seed item without one: 400 on <c>NurseryBatchId</c> with SEED_ITEM_WITHOUT_PRODUCT, naming the item so the user knows which to open in Vật tư và Giống.
    /// The batch comes from <see cref="CheckBatchAsync"/>, with its seed lot, the item and the item's product loaded.
    /// </summary>
    private static (Product? Product, AppError? Error) ProductOfBatch(NurseryBatch batch)
    {
        var item = batch.SeedLotLine.Item;
        if (item?.Product is { } product)
        {
            return (product, null);
        }

        var invalid = AppError.Invalid(BatchProductField, "Giống này chưa gắn loại sản phẩm bán. Vào Vật tư & Giống, mở giống đó và chọn loại hàu bán ra trước.");
        return (null, invalid with
        {
            Details = new Dictionary<string, object?>(invalid.Details!)
            {
                ["reasonCode"] = SeedItemWithoutProductReason,
                ["nurseryBatchId"] = batch.Id,
                ["nurseryCode"] = batch.Code,
                ["seedItemName"] = item?.Name,
            },
        });
    }

    private static void ApplyProduct(CultivationCycle cycle, Product product)
    {
        cycle.ProductId = product.Id;
        cycle.Product = product;
    }
}
