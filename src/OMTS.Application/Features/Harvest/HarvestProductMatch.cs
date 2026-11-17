using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// A harvest lot holds one kind of oyster (PM decision of 17/11/2026): the product of the cycle (the product of its seed). A lot split into product lines holds at most one line, of that product. A lot with no lines,
/// or a cycle with no product (started before the decision), is not checked for the product, but never holds more than one line.
/// </summary>
internal static class HarvestProductMatch
{
    public const string ReasonCode = "HARVEST_PRODUCT_MISMATCH";

    public const string TooManyLinesHint = "A harvest lot holds only the oyster of its cycle: one product line";

    /// <summary>True when the lot has no lines, or one line that is of the product of the cycle (any product when the cycle has none).</summary>
    public static bool Matches(Guid? cycleProductId, IReadOnlyList<ResolvedProductLine>? lines) =>
        lines is not { Count: > 0 } || (lines.Count == 1 && (cycleProductId is not { } cycleProduct || lines[0].Product.Id == cycleProduct));

    /// <summary>
    /// MSG-GEN-01 on <c>Product lines</c> when the form sends more than one line, 400 MSG-GEN-09 with the reason code when the line is not the product of the cycle, or null when the lines are fine. Needs the cycle
    /// loaded with its product.
    /// </summary>
    internal static AppError? Refusal(CultivationCycle cycle, IReadOnlyList<ResolvedProductLine>? lines)
    {
        if (lines is { Count: > 1 })
        {
            return AppError.Invalid(HarvestLotProductLineService.LinesField, TooManyLinesHint);
        }

        if (Matches(cycle.ProductId, lines))
        {
            return null;
        }

        var cycleProduct = cycle.Product?.Name ?? "loại hàu khác";
        return new AppError(
            ErrorKind.Validation,
            UserMessages.HarvestRefused($"Lô thu hoạch chỉ chứa loại hàu bè đang nuôi ({cycleProduct})."),
            new Dictionary<string, object?>
            {
                ["field"] = "Product lines",
                ["reasonCode"] = ReasonCode,
                ["cycleProductId"] = cycle.ProductId,
                ["cycleProductName"] = cycleProduct,
            });
    }
}
