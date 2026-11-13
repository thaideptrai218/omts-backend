using System.Globalization;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The cages of a harvest lot match the purchase request (owner decision of 13/11/2026): the lots of a request never add up to more than the cages it accepted, and a request that has all its
/// cages (15/15) leaves the list of requests to harvest for. A request with no accepted cages (before cages were accepted) is not checked. Marking the harvest done stays the Owner's act.
/// </summary>
internal static class HarvestOrderCages
{
    public const string ReasonCode = "HARVEST_ORDER_CAGES_EXCEEDED";

    /// <summary>True when the request has accepted cages and the lots recorded for it already reach them.</summary>
    public static bool Reached(int? acceptedCages, decimal harvestedCages) => acceptedCages is > 0 && harvestedCages >= acceptedCages;

    /// <summary>
    /// 400 MSG-GEN-09 on the cages field when <paramref name="harvestedByOthers"/> + <paramref name="lotCages"/> goes over what the request accepted; else null.
    /// <paramref name="harvestedByOthers"/> is every lot of the request but the one being recorded or corrected.
    /// </summary>
    public static AppError? Refusal(PurchaseRequest order, decimal harvestedByOthers, decimal lotCages)
    {
        if (order.AcceptedCages is not > 0 || harvestedByOthers + lotCages <= order.AcceptedCages)
        {
            return null;
        }

        var accepted = order.AcceptedCages.Value;
        var remaining = Math.Max(0m, accepted - harvestedByOthers);
        return new AppError(
            ErrorKind.Validation,
            UserMessages.HarvestRefused(
                $"Đơn mua {order.Code} chỉ còn {Format(remaining)} lồng (đã thu {Format(harvestedByOthers)}/{accepted} lồng). Số lồng thu hoạch phải khớp với đơn mua."),
            new Dictionary<string, object?>
            {
                ["field"] = "CageCount",
                ["reasonCode"] = ReasonCode,
                ["requestId"] = order.Id,
                ["requestCode"] = order.Code,
                ["acceptedCages"] = accepted,
                ["harvestedCages"] = harvestedByOthers,
                ["remainingCages"] = remaining,
            });
    }

    private static string Format(decimal cages) => cages.ToString("0.##", CultureInfo.InvariantCulture);
}
