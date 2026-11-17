using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The product of the cycle (the product of the seed item, owner decision of 13/11/2026) is the main product of a lot: a lot split into product lines must hold it, and may hold lines of other products
/// beside it. A lot with no lines, or a cycle with no product (started before the decision), is not checked.
/// </summary>
internal static class HarvestProductMatch
{
    public const string ReasonCode = "HARVEST_PRODUCT_MISMATCH";

    /// <summary>True when the cycle has no product, the lot has no lines, or one of the lines is the product of the cycle.</summary>
    public static bool Matches(Guid? cycleProductId, IReadOnlyList<ResolvedProductLine>? lines) =>
        cycleProductId is not { } cycleProduct || lines is not { Count: > 0 } || lines.Any(l => l.Product.Id == cycleProduct);

    /// <summary>400 MSG-GEN-09 with the reason code, or null when the lines hold the product of the cycle. Needs the cycle loaded with its product.</summary>
    internal static AppError? Refusal(CultivationCycle cycle, IReadOnlyList<ResolvedProductLine>? lines)
    {
        if (Matches(cycle.ProductId, lines))
        {
            return null;
        }

        var cycleProduct = cycle.Product?.Name ?? "loại hàu khác";
        return new AppError(
            ErrorKind.Validation,
            UserMessages.HarvestRefused($"Lô thu hoạch phải có dòng của loại hàu bè đang nuôi ({cycleProduct}); các loại khác chỉ thêm vào."),
            new Dictionary<string, object?>
            {
                ["field"] = "Product lines",
                ["reasonCode"] = ReasonCode,
                ["cycleProductId"] = cycle.ProductId,
                ["cycleProductName"] = cycleProduct,
            });
    }
}
