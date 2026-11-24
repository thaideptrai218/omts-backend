using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// A lot that has a trader is invoiced only once that trader agreed to its weighing (owner decision of 24/11/2026; <see cref="TraderWeighingService"/>). A lot with no trader (walk-in orders, a lot
/// outside a plan, or orders of several traders) needs no agreement.
/// </summary>
public sealed class HarvestLotAgreementGuard(IHarvestLotRepository lots)
{
    public const string ReasonCode = "WEIGHING_NOT_AGREED";

    /// <summary>409 MSG-GEN-09 naming the first lot whose trader has not agreed to the weighing it has now (reason code <see cref="ReasonCode"/>, <c>lotCode</c>); null when every lot may be invoiced.</summary>
    public async Task<AppError?> EnsureAgreedAsync(IEnumerable<HarvestLot> chosen, CancellationToken cancellationToken)
    {
        var pending = chosen.Where(l => !l.IsWeighingAgreed).DistinctBy(l => l.Id).ToList();
        if (pending.Count == 0)
        {
            return null;
        }

        var orders = await lots.OrdersOfAsync([.. pending.Select(l => l.Id)], cancellationToken);
        if (pending.FirstOrDefault(l => orders[l.Id].Trader is not null) is not { } lot)
        {
            return null;
        }

        return new AppError(
            ErrorKind.Conflict,
            UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", "awaiting the trader's agreement on its weighing"),
            new Dictionary<string, object?> { ["reasonCode"] = ReasonCode, ["lotCode"] = lot.LotCode });
    }
}
