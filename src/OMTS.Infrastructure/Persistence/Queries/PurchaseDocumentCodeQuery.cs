using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// Codes are compared upper-cased, as the catalogue compares its codes, so the in-memory test provider answers like SQL Server's
/// case-insensitive collation; the unique index on each Code column still refuses a parallel save of the same code.
/// </summary>
internal sealed class PurchaseDocumentCodeQuery(OmtsDbContext db) : IPurchaseDocumentCodeQuery
{
    public async Task<Guid?> FindAsync(PurchaseDocumentKind kind, string code, Guid? exceptId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        var ids = kind switch
        {
            PurchaseDocumentKind.Receipt => db.PurchaseReceipts.AsNoTracking().Where(x => x.Code.ToUpper() == upper && x.Id != exceptId).Select(x => x.Id),
            PurchaseDocumentKind.Return => db.PurchaseReturns.AsNoTracking().Where(x => x.Code.ToUpper() == upper && x.Id != exceptId).Select(x => x.Id),
            PurchaseDocumentKind.Payment => db.SupplierPayments.AsNoTracking().Where(x => x.Code.ToUpper() == upper && x.Id != exceptId).Select(x => x.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return await ids.Cast<Guid?>().FirstOrDefaultAsync(cancellationToken);
    }
}
