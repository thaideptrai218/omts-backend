using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>
/// The product a cycle will yield and sell (owner decision of 13/11/2026): required when a cycle is started, changeable only before the first seeding record (the same lock as the nursery batch), and the one a harvest lot's
/// purchase request must match. Errors name the field <c>ProductId</c>.
/// </summary>
public sealed partial class CultivationCycleService
{
    private const string ProductField = "ProductId";

    /// <summary>The product to start a cycle with: chosen, existing and In Use. Null product and a 400 on <c>ProductId</c> otherwise.</summary>
    private async Task<(Product? Product, AppError? Error)> CheckProductAsync(Guid? productId, CancellationToken cancellationToken)
    {
        if (productId is not { } id || id == Guid.Empty)
        {
            return (null, AppError.Invalid(ProductField, "Chọn loại hàu mà chu kỳ này nuôi và bán."));
        }

        var product = await products.GetForEditAsync(id, cancellationToken);
        if (product is null)
        {
            return (null, AppError.Invalid(ProductField, "Loại hàu đã chọn không còn trong danh mục sản phẩm."));
        }

        return product.StatusId == StatusIds.Usage.InUse
            ? (product, null)
            : (null, AppError.Invalid(ProductField, $"Sản phẩm {product.Name} đã ngừng bán. Chọn loại hàu khác."));
    }

    private static void ApplyProduct(CultivationCycle cycle, Product? changed)
    {
        if (changed is not null)
        {
            cycle.ProductId = changed.Id;
            cycle.Product = changed;
        }
    }

    /// <summary>
    /// The product an edit asks for. Left out (null) or the same as now: no change (null product, no error). A different one needs a cycle with no seeding record (else 409 CYCLE_SEED_LOCKED) and a usable product.
    /// </summary>
    private async Task<(Product? Changed, AppError? Error)> CheckProductChangeAsync(CultivationCycle cycle, Guid? wanted, CancellationToken cancellationToken)
    {
        if (wanted is not { } id || id == cycle.ProductId)
        {
            return (null, null);
        }

        if ((await nurseries.SeededByCycleAsync([cycle.Id], cancellationToken)).GetValueOrDefault(cycle.Id) > 0m)
        {
            return (null, NurseryRules.Refused(
                "Chu kỳ đã có bản ghi thả giống nên không thể đổi loại hàu nuôi. Hãy xóa các bản ghi thả giống trước.", NurseryReasonCodes.CycleSeedLocked,
                new Dictionary<string, object?> { ["field"] = ProductField, ["cycleId"] = cycle.Id }));
        }

        return await CheckProductAsync(id, cancellationToken);
    }
}
