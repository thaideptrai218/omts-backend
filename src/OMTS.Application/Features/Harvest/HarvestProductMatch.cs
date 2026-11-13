using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// A harvest lot is recorded only for a purchase request of the product the cycle farms (owner decision of 13/11/2026: a customer who ordered "Hàu múp" must not get a lot of "Hàu gầy 3cm"). A cycle with no product (started before the
/// decision) is not checked.
/// </summary>
internal static class HarvestProductMatch
{
    public const string ReasonCode = "HARVEST_PRODUCT_MISMATCH";

    /// <summary>True when the cycle has no product or the request is for the same product; also the <c>matchesCycleProduct</c> of the order list.</summary>
    public static bool Matches(Guid? cycleProductId, Guid requestProductId) => cycleProductId is not { } cycleProduct || cycleProduct == requestProductId;

    /// <summary>400 MSG-GEN-09 with the reason code, or null when the request matches the cycle. Needs the cycle loaded with its product.</summary>
    public static AppError? Refusal(CultivationCycle cycle, PurchaseRequest request)
    {
        if (Matches(cycle.ProductId, request.ProductId))
        {
            return null;
        }

        var cycleProduct = cycle.Product?.Name ?? "loại hàu khác";
        return new AppError(
            ErrorKind.Validation,
            UserMessages.HarvestRefused($"Đơn mua {request.Code} đặt {request.ProductName} nhưng bè đang nuôi {cycleProduct}. Chọn đơn mua khớp loại hàu đang nuôi."),
            new Dictionary<string, object?>
            {
                ["field"] = "Purchase request",
                ["reasonCode"] = ReasonCode,
                ["requestId"] = request.Id,
                ["requestCode"] = request.Code,
                ["requestProductName"] = request.ProductName,
                ["cycleProductName"] = cycleProduct,
            });
    }
}
