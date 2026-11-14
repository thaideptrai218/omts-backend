using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ISalesReturnRepository
{
    void Add(SalesReturn salesReturn);

    /// <summary>The return with its lines, tracked, with its invoice (customer and lines) and the lot of each line: what the cancellation changes and checks (CLAIM-03.3).</summary>
    Task<SalesReturn?> GetForEditAsync(Guid returnId, CancellationToken cancellationToken);

    /// <summary>The return with its lines (lots) and invoice (lines with product and lot), read only.</summary>
    Task<SalesReturn?> GetDetailAsync(Guid returnId, CancellationToken cancellationToken);
}
