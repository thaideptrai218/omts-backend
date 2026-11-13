using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ISupplierRepository
{
    Task<Supplier?> GetForEditAsync(Guid supplierId, CancellationToken cancellationToken);

    /// <summary>Another supplier with this phone number.</summary>
    Task<Supplier?> FindByPhoneAsync(string phoneNumber, Guid? exceptSupplierId, CancellationToken cancellationToken);

    /// <summary>The supplier holding this code, whatever its letter case, other than <paramref name="exceptSupplierId"/>.</summary>
    Task<Supplier?> FindByCodeAsync(string code, Guid? exceptSupplierId, CancellationToken cancellationToken);

    /// <summary>Which of these phone numbers already belong to a supplier, discontinued ones included (one query for a whole file).</summary>
    Task<IReadOnlySet<string>> ExistingPhonesAsync(IReadOnlyCollection<string> phoneNumbers, CancellationToken cancellationToken);

    /// <summary>One page of the matching suppliers; filter, sort and paging, also on the money figures, run in the database.</summary>
    Task<(IReadOnlyList<SupplierFigureRow> Items, int Total)> SearchAsync(SupplierSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Every matching supplier, in the order of the filter (the export).</summary>
    Task<IReadOnlyList<SupplierFigureRow>> ListAsync(SupplierSearchFilter filter, CancellationToken cancellationToken);

    /// <summary>One page of the purchase receipts of the supplier, any status, and its returns except Drafts, newest first.</summary>
    Task<(IReadOnlyList<SupplierHistoryRow> Items, int Total)> HistoryAsync(Guid supplierId, int page, int pageSize, CancellationToken cancellationToken);

    void Add(Supplier supplier);
}

public enum SupplierSort
{
    Name,
    Code,
    Phone,
    Type,
    Province,
    CreatedAt,
    Status,
    TotalPurchased,
    CurrentDebt,
}

/// <summary>The supplier list filters; null means "any". Name ascending is the default order.</summary>
/// <param name="Search">Name, phone or code (contains).</param>
/// <param name="Today">The day the current debt is read at.</param>
/// <param name="PurchaseFrom">With <paramref name="PurchaseTo"/>: only suppliers with a Completed receipt in the period; the total purchased then covers that period.</param>
public sealed record SupplierSearchFilter(
    string? Search,
    SupplierType? Type,
    int? StatusId,
    DateOnly Today,
    DateOnly? PurchaseFrom,
    DateOnly? PurchaseTo,
    decimal? TotalPurchasedFrom,
    decimal? TotalPurchasedTo,
    decimal? CurrentDebtFrom,
    decimal? CurrentDebtTo,
    SupplierSort Sort = SupplierSort.Name,
    bool Descending = false);

/// <summary>A supplier with its money figures summed by SQL. Callers strip them for accounts without the permission.</summary>
public sealed record SupplierFigureRow(Supplier Supplier, decimal TotalPurchased, decimal CurrentDebt);

/// <param name="ReceiptId">The receipt itself, or the receipt a return was made from; null on a return without a receipt.</param>
/// <param name="Total">Net of the discount of the receipt or return.</param>
public sealed record SupplierHistoryRow(
    Guid Id, Guid? ReceiptId, bool IsReturn, string Code, DateOnly Date, DateTimeOffset CreatedAt, Guid RecordedByAccountId, decimal Total, int StatusId);

public interface IFarmingInputItemRepository
{
    /// <summary>The item with its units (and their catalogue rows), photos and group, tracked.</summary>
    Task<FarmingInputItem?> GetForEditAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>The items with their units, untracked.</summary>
    Task<IReadOnlyList<FarmingInputItem>> GetByIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>The items with their units, tracked: the ones whose cost the ledger changes.</summary>
    Task<IReadOnlyList<FarmingInputItem>> GetByIdsForUpdateAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>Another item whose code equals <paramref name="code"/> ignoring case, discontinued items included.</summary>
    Task<FarmingInputItem?> FindByCodeAsync(string code, Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>Another item whose name equals <paramref name="name"/> ignoring case, discontinued items included.</summary>
    Task<FarmingInputItem?> FindByNameAsync(string name, Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>The item that owns a code, as an item code or as the code of one of its units (ignoring case), other than the given item and item unit.</summary>
    Task<CodeOwner?> FindCodeOwnerAsync(string code, Guid? exceptItemId, Guid? exceptItemUnitId, CancellationToken cancellationToken);

    /// <summary>The items that are not deleted, by code or name or a unit code (contains), optional type and status, ordered by code; units loaded.</summary>
    Task<IReadOnlyList<FarmingInputItem>> ListAsync(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken);

    /// <summary>True when the item is on a receipt line or a material usage record: its type is then fixed.</summary>
    Task<bool> IsUsedAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Which of the items are on a receipt line or a material usage record (one query for a whole list).</summary>
    Task<IReadOnlySet<Guid>> UsedAmongAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>The item units of these items that a receipt or return line names: their factor and unit are then fixed.</summary>
    Task<IReadOnlySet<Guid>> UsedUnitsAmongAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>
    /// The price of one base unit on the latest line (by time received) of a Completed receipt, per item, leaving out the receipt <paramref name="exceptReceiptId"/>;
    /// an item with no such line is absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> LatestPurchasePricesAsync(IReadOnlyCollection<Guid> itemIds, Guid? exceptReceiptId, CancellationToken cancellationToken);

    /// <summary>Whether any receipt line, return line, issue or movement row names the item.</summary>
    Task<bool> HasMovementsAsync(Guid itemId, CancellationToken cancellationToken);

    void Add(FarmingInputItem item);

    /// <summary>A new unit under a tracked item, added explicitly because its key is already set.</summary>
    void AddUnit(InputItemUnit unit);

    void RemoveUnit(InputItemUnit unit);

    void AddPhoto(InputItemPhoto photo);

    void RemovePhoto(InputItemPhoto photo);
}

/// <param name="ItemId">The item whose code or unit code it is.</param>
/// <param name="IsUnitCode">True when the code is that of a unit of the item.</param>
public sealed record CodeOwner(Guid ItemId, bool IsUnitCode);
/// <summary>
/// PURCHASE-03 filters; null means "any". The zone filters only: access follows the role (BR-FARM-08). The item group, item and
/// raft filters are line filters: a receipt matches when one of its lines does, and only those lines count in the totals.
/// </summary>
/// <param name="Search">Receipt code or supplier name (contains).</param>
public sealed record PurchaseReceiptFilter(
    InputItemGroup? ItemGroup = null,
    Guid? ItemId = null,
    Guid? RaftId = null,
    Guid? SupplierId = null,
    int? StatusId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Code = null,
    string? Search = null,
    IReadOnlyList<int>? StatusIds = null,
    IReadOnlyList<InputItemGroup>? ItemGroups = null,
    IReadOnlyList<Guid>? ZoneIds = null,
    PurchaseReceiptSort Sort = PurchaseReceiptSort.Date,
    bool Descending = true);

public enum PurchaseReceiptSort
{
    Date,
    Code,
    Supplier,
    Total,
    Status,
}

public enum PurchaseReturnSort
{
    Date,
    Code,
    Supplier,
    Total,
    Status,
}

/// <summary>The purchase return list filters; null means "any". The period is on the return date.</summary>
public sealed record PurchaseReturnFilter(
    string? Search, DateOnly From, DateOnly To, Guid? SupplierId, IReadOnlyList<int>? StatusIds, IReadOnlyList<Guid>? ZoneIds,
    PurchaseReturnSort Sort, bool Descending, bool? FromReceipt = null);

public interface IPurchaseReceiptRepository
{
    /// <summary>Receipt with supplier, zone, lines (items, rafts and their zones) and returns (lines), tracked.</summary>
    Task<PurchaseReceipt?> GetForEditAsync(Guid receiptId, CancellationToken cancellationToken);

    /// <summary>One page (1-based) of matching receipts with figures computed in SQL, newest first, and the totals of all matches.</summary>
    Task<(IReadOnlyList<ReceiptFigureRow> Items, ReceiptListTotals Totals)> PageFiguresAsync(
        PurchaseReceiptFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Matching receipts with figures computed in SQL, newest first, at most <paramref name="take"/> when set.</summary>
    Task<IReadOnlyList<ReceiptFigureRow>> ListFiguresAsync(PurchaseReceiptFilter filter, int? take, CancellationToken cancellationToken);

    /// <summary>The totals of every matching receipt (PURCHASE-03 step 3).</summary>
    Task<ReceiptListTotals> TotalsAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken);

    void Add(PurchaseReceipt receipt);

    /// <summary>Deletes a Draft; its lines are removed with <see cref="RemoveLine"/> first (foreign keys never cascade).</summary>
    void Remove(PurchaseReceipt receipt);

    void AddReturn(PurchaseReturn purchaseReturn);

    /// <summary>New rows under a tracked receipt are added explicitly: their keys are already set, so EF would
    /// otherwise treat them as existing rows.</summary>
    void AddLine(PurchaseReceiptLine line);

    /// <summary>Deletes a line of a Draft (foreign keys never cascade).</summary>
    void RemoveLine(PurchaseReceiptLine line);

    /// <summary>A purchase return with its lines and their items, its receipt, supplier and zone, untracked.</summary>
    Task<PurchaseReturn?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken);

    /// <summary>A purchase return with its lines, tracked.</summary>
    Task<PurchaseReturn?> GetReturnForEditAsync(Guid returnId, CancellationToken cancellationToken);

    /// <summary>The money paid back for this return, any status, oldest first.</summary>
    Task<IReadOnlyList<SupplierPayment>> RefundsOfReturnAsync(Guid returnId, CancellationToken cancellationToken);

    /// <summary>One page (1-based) of the matching returns and the net value of the Recorded ones of every match.</summary>
    Task<(IReadOnlyList<PurchaseReturnFigureRow> Items, int Total, decimal TotalValue)> SearchReturnsAsync(
        PurchaseReturnFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Every matching return, in the order of the filter (the export).</summary>
    Task<IReadOnlyList<PurchaseReturnFigureRow>> ListReturnsAsync(PurchaseReturnFilter filter, CancellationToken cancellationToken);

    /// <summary>Deletes a Draft return; its lines are removed with <see cref="RemoveReturnLine"/> first.</summary>
    void RemoveReturn(PurchaseReturn purchaseReturn);

    /// <summary>A new line under a tracked return, added explicitly because its key is already set.</summary>
    void AddReturnLine(PurchaseReturnLine line);

    void RemoveReturnLine(PurchaseReturnLine line);
}

/// <summary>A part of a payment set against a receipt; <paramref name="ReceiptPayable"/> is the receipt's total less its discount.</summary>
public sealed record PaymentAllocationRow(Guid ReceiptId, string ReceiptCode, DateTimeOffset ReceivedAt, decimal ReceiptPayable, decimal Amount);

public interface ISupplierPaymentRepository
{
    /// <summary>The payment with its supplier and its allocations, tracked.</summary>
    Task<SupplierPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>The receipts the payment pays, in the order received, each with its total less its discount.</summary>
    Task<IReadOnlyList<PaymentAllocationRow>> AllocationsOfAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<SupplierPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>The latest payment that is not cancelled with this bank reference, whatever its supplier (PURCHASE-05.4).</summary>
    Task<SupplierPayment?> FindRecordedByBankReferenceAsync(string bankReference, CancellationToken cancellationToken);

    /// <summary>Payments by supplier, kind, method, status and date range, newest first, with the Recorded sums per kind of all matches.</summary>
    Task<(IReadOnlyList<SupplierPayment> Items, int Total, decimal TotalPaid, decimal TotalPaidBack)> SearchAsync(
        Guid? supplierId, SupplierPaymentKind? kind, PaymentMethod? method, int? statusId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken cancellationToken);

    void Add(SupplierPayment payment);
}

