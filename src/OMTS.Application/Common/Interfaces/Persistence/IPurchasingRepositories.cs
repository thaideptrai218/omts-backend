using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ISupplierRepository
{
    Task<Supplier?> GetForEditAsync(Guid supplierId, CancellationToken cancellationToken);

    /// <summary>Another supplier with this phone number.</summary>
    Task<Supplier?> FindByPhoneAsync(string phoneNumber, Guid? exceptSupplierId, CancellationToken cancellationToken);

    /// <summary>By name or phone (contains), optional type and status, ordered by name.</summary>
    Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(
        string? search, SupplierType? type, int? statusId, int page, int pageSize, CancellationToken cancellationToken);

    void Add(Supplier supplier);
}

public interface IFarmingInputItemRepository
{
    Task<FarmingInputItem?> GetForEditAsync(Guid itemId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FarmingInputItem>> GetByIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>Another item whose code equals <paramref name="code"/> ignoring case, discontinued items included.</summary>
    Task<FarmingInputItem?> FindByCodeAsync(string code, Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>Another item whose name equals <paramref name="name"/> ignoring case, discontinued items included.</summary>
    Task<FarmingInputItem?> FindByNameAsync(string name, Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>
    /// The unit of the Seed items other than <paramref name="exceptItemId"/>, discontinued ones included: the unit of the
    /// first one created, or null when there is no other Seed item (BR-PURCHASE-10).
    /// </summary>
    Task<string?> SeedUnitAsync(Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>Every item code that starts with <paramref name="prefix"/> ignoring case, discontinued items included.</summary>
    Task<IReadOnlyList<string>> CodesStartingWithAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>By code or name (contains), optional group and status, ordered by code.</summary>
    Task<IReadOnlyList<FarmingInputItem>> ListAsync(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken);

    /// <summary>True when the item is on a receipt line or a material usage record: its group and unit are then fixed.</summary>
    Task<bool> IsUsedAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Which of the items are on a receipt line or a material usage record (one query for a whole list).</summary>
    Task<IReadOnlySet<Guid>> UsedAmongAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    void Add(FarmingInputItem item);
}

/// <summary>
/// PURCHASE-03 filters; null means "any". Receipts have no zone, so no zone limit (BR-FARM-08). The item group, item and
/// raft filters are line filters: a receipt matches when one of its lines does, and only those lines count in the totals.
/// </summary>
public sealed record PurchaseReceiptFilter(
    InputItemGroup? ItemGroup = null,
    Guid? ItemId = null,
    Guid? RaftId = null,
    Guid? SupplierId = null,
    int? StatusId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Code = null);

public interface IPurchaseReceiptRepository
{
    /// <summary>Receipt with supplier, lines (items, rafts and their zones) and returns (lines), tracked.</summary>
    Task<PurchaseReceipt?> GetForEditAsync(Guid receiptId, CancellationToken cancellationToken);

    /// <summary>One page (1-based) of matching receipts with figures computed in SQL, newest first, and the totals of all matches.</summary>
    Task<(IReadOnlyList<ReceiptFigureRow> Items, ReceiptListTotals Totals)> PageFiguresAsync(
        PurchaseReceiptFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Matching receipts with figures computed in SQL, newest first, at most <paramref name="take"/> when set.</summary>
    Task<IReadOnlyList<ReceiptFigureRow>> ListFiguresAsync(PurchaseReceiptFilter filter, int? take, CancellationToken cancellationToken);

    /// <summary>The totals of every matching receipt (PURCHASE-03 step 3).</summary>
    Task<ReceiptListTotals> TotalsAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Material lines that name a raft, of Completed receipts received in the period, and the returns of such lines dated in
    /// the period (BR-FARM-05). The raft, item and supplier filters apply; the group is always Material.
    /// </summary>
    Task<(IReadOnlyList<RaftCostLineRow> Lines, IReadOnlyList<RaftCostReturnRow> Returns)> RaftMaterialCostRowsAsync(
        PurchaseReceiptFilter filter, CancellationToken cancellationToken);

    void Add(PurchaseReceipt receipt);

    /// <summary>Deletes a Draft; its lines are removed with <see cref="RemoveLine"/> first (foreign keys never cascade).</summary>
    void Remove(PurchaseReceipt receipt);

    void AddReturn(PurchaseReturn purchaseReturn);

    /// <summary>New rows under a tracked receipt are added explicitly: their keys are already set, so EF would
    /// otherwise treat them as existing rows.</summary>
    void AddLine(PurchaseReceiptLine line);

    /// <summary>Deletes a line of a Draft (foreign keys never cascade).</summary>
    void RemoveLine(PurchaseReceiptLine line);

    /// <summary>A purchase return with its lines (and their receipt lines and items) and its receipt, untracked.</summary>
    Task<PurchaseReturn?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken);
}

public interface ISupplierPaymentRepository
{
    /// <summary>The payment with its supplier, tracked.</summary>
    Task<SupplierPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<SupplierPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>The latest payment that is not cancelled with this bank reference, whatever its supplier (PURCHASE-05.4).</summary>
    Task<SupplierPayment?> FindRecordedByBankReferenceAsync(string bankReference, CancellationToken cancellationToken);

    /// <summary>Payments by supplier, kind, method, status and date range, newest first, with the Recorded sums per kind of all matches.</summary>
    Task<(IReadOnlyList<SupplierPayment> Items, int Total, decimal TotalPaid, decimal TotalPaidBack)> SearchAsync(
        Guid? supplierId, SupplierPaymentKind? kind, PaymentMethod? method, int? statusId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken cancellationToken);

    void Add(SupplierPayment payment);
}
