using OMTS.Application.Common.Models.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IPurchaseReceiptRepository
{
    /// <summary>Receipt with supplier, zone, raft, lines (items, seed lots), returns (lines) and payments, tracked.</summary>
    Task<PurchaseReceipt?> GetForEditAsync(Guid receiptId, CancellationToken cancellationToken);

    /// <summary>Codes already used that start with <paramref name="prefix"/> (daily code sequence).</summary>
    Task<int> CountCodesAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Return codes already used that start with <paramref name="prefix"/>.</summary>
    Task<int> CountReturnCodesAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Per seed line: stocked quantity, and whether a seeding without a quantity uses the lot (counts as fully stocked).</summary>
    Task<IReadOnlyDictionary<Guid, (decimal Stocked, bool UnknownQuantity)>> StockedAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken);

    /// <summary>Matching receipts with lines, returns and payments, newest first, untracked (raft material cost only: it needs the lines).</summary>
    Task<IReadOnlyList<PurchaseReceipt>> SearchAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken);

    /// <summary>One page (1-based) of matching receipts with figures computed in SQL, newest first, and the totals of all matches.</summary>
    Task<(IReadOnlyList<ReceiptFigureRow> Items, ReceiptFigureTotals Totals)> PageFiguresAsync(
        PurchaseReceiptFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Matching receipts with figures computed in SQL, newest first, at most <paramref name="take"/> when set.</summary>
    Task<IReadOnlyList<ReceiptFigureRow>> ListFiguresAsync(PurchaseReceiptFilter filter, int? take, CancellationToken cancellationToken);

    /// <summary>Completed receipts of one supplier with figures, oldest first.</summary>
    Task<IReadOnlyList<ReceiptFigureRow>> CompletedFiguresOfSupplierAsync(Guid supplierId, CancellationToken cancellationToken);

    /// <summary>Per supplier (or all when null) the sums over its Completed receipts, computed in SQL.</summary>
    Task<IReadOnlyList<SupplierDebtFigures>> SupplierDebtFiguresAsync(IReadOnlyCollection<Guid>? supplierIds, CancellationToken cancellationToken);

    void Add(PurchaseReceipt receipt);

    void AddReturn(PurchaseReturn purchaseReturn);

    /// <summary>New rows under a tracked receipt are added explicitly: their keys are already set, so EF would
    /// otherwise treat them as existing rows.</summary>
    void AddLine(PurchaseReceiptLine line);

    void AddSeedLot(Domain.Entities.Cultivation.SeedReceiptLot lot);

    /// <summary>Deletes a line of a Draft (foreign keys never cascade).</summary>
    void RemoveLine(PurchaseReceiptLine line);

    /// <summary>A purchase return with its lines (and their receipt lines and items) and its receipt, untracked.</summary>
    Task<PurchaseReturn?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken);
}
