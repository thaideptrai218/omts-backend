using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace OMTS.Infrastructure.Persistence.Queries;

internal static class PurchaseReturnQueries
{
    /// <summary>The lines of purchase returns that count: a Cancelled return counts nowhere (BR-PURCHASE-07).</summary>
    public static IQueryable<PurchaseReturnLine> RecordedReturnLines(this OmtsDbContext db) =>
        db.PurchaseReturnLines.AsNoTracking().Where(l => l.Return.StatusId == StatusIds.PurchaseReturn.Recorded);
}
