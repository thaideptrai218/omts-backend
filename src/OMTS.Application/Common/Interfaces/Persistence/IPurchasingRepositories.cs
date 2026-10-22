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

    /// <summary>Another item whose code equals <paramref name="code"/> ignoring case.</summary>
    Task<FarmingInputItem?> FindByCodeAsync(string code, Guid? exceptItemId, CancellationToken cancellationToken);

    /// <summary>By code or name (contains), optional group and status, ordered by code.</summary>
    Task<IReadOnlyList<FarmingInputItem>> ListAsync(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken);

    /// <summary>True when the item is on a receipt line or is the planned seed of a cultivation cycle.</summary>
    Task<bool> IsUsedAsync(Guid itemId, CancellationToken cancellationToken);

    void Add(FarmingInputItem item);
}

/// <summary>INP-07 filters; null means "any". Zones are limited to <see cref="Zones"/> as well.</summary>
public sealed record PurchaseReceiptFilter(
    PermittedZones Zones,
    InputItemGroup? ItemGroup = null,
    Guid? ItemId = null,
    Guid? ZoneId = null,
    Guid? RaftId = null,
    Guid? SupplierId = null,
    int? StatusId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Code = null);

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

    /// <summary>Matching receipts with lines, returns and payments, newest first, untracked.</summary>
    Task<IReadOnlyList<PurchaseReceipt>> SearchAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken);

    /// <summary>Completed receipts of the suppliers (or all suppliers when null), with lines, returns and payments.</summary>
    Task<IReadOnlyList<PurchaseReceipt>> CompletedOfSuppliersAsync(IReadOnlyCollection<Guid>? supplierIds, CancellationToken cancellationToken);

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

public interface ISupplierPaymentRepository
{
    Task<SupplierPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<SupplierPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>Payments by supplier, receipt, zone (of the receipt), method, status and date range, newest first.</summary>
    Task<(IReadOnlyList<SupplierPayment> Items, int Total, decimal TotalAmount)> SearchAsync(
        Guid? supplierId, Guid? receiptId, Guid? zoneId, PaymentMethod? method, int? statusId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Recorded payments not allocated to any receipt, per supplier.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> UnallocatedAsync(IReadOnlyCollection<Guid>? supplierIds, CancellationToken cancellationToken);

    void Add(SupplierPayment payment);
}
