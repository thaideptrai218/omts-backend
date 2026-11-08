using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The kg of a harvest lot held by sales awaiting their VietQR transfer (SALE-05 step 7). They are not invoiced yet, but the transfer will invoice them, so the lot is not removed
/// (HARVEST-01.1) and the daily close does not count them as leftover (GFR-05, BR-HARVEST-07).
/// </summary>
public sealed class PendingSaleLotQuery(IPendingSaleRepository sales)
{
    /// <summary>The sales awaiting their transfer that hold the lot, and their kg of it; (0, 0) when none.</summary>
    public async Task<(int Sales, decimal Kg)> AwaitingAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var (count, kg) = (0, 0m);
        foreach (var sale in await sales.ListAwaitingNamingLotAsync(lotId, cancellationToken))
        {
            var lines = (PendingSaleSnapshot.Read(sale).Request.Lines ?? []).Where(l => l?.HarvestLotId == lotId).ToList();
            if (lines.Count > 0)
            {
                count++;
                kg += lines.Sum(l => l!.Kg ?? 0m);
            }
        }

        return (count, kg);
    }
}
