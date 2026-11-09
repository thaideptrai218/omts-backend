using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// One supplier's sums as of a date (BR-PURCHASE-04), computed by SQL over Completed receipts, Recorded returns of the supplier and
/// Recorded payments. Draft and Cancelled receipts, Draft and Cancelled returns and Cancelled payments never count, also in past periods.
/// </summary>
/// <param name="Receipts">Σ totals less discounts of the Completed receipts received by the date.</param>
/// <param name="Returned">Σ amounts less discounts of the Recorded purchase returns dated by the date, with or without a receipt.</param>
/// <param name="Paid">Σ amounts and discounts of Recorded payments of kind Payment dated by the date.</param>
/// <param name="PaidBack">Σ Recorded payments of kind PaidBack dated by the date.</param>
/// <param name="StandaloneReturned">The part of <paramref name="Returned"/> made without a receipt, which no receipt absorbs.</param>
public sealed record SupplierDebtSums(decimal Receipts, decimal Returned, decimal Paid, decimal PaidBack, decimal StandaloneReturned = 0)
{
    public static readonly SupplierDebtSums Zero = new(0, 0, 0, 0);

    /// <summary>What the cooperative owes; below zero when it has paid in advance.</summary>
    public decimal Debt => Receipts - Returned - Paid + PaidBack;

    /// <summary>What is available to cover receipts: the money the supplier holds and the returns no receipt absorbs (BR-PURCHASE-04); below zero when it paid back more than it was paid.</summary>
    public decimal Credit => Paid - PaidBack + StandaloneReturned;
}

/// <summary>A supplier that may be listed on the debt list.</summary>
public sealed record DebtSupplierRow(Guid Id, string Name, string PhoneNumber, int StatusId);

/// <summary>A Completed receipt with its total, its discount and the amount of all its Recorded returns less their discounts.</summary>
/// <param name="ReceivedAt">When the goods were received, at UTC+7; <paramref name="ReceiptDate"/> is its date.</param>
public sealed record DebtReceiptRow(
    Guid SupplierId, Guid Id, string Code, DateOnly ReceiptDate, decimal Total, decimal Returned, decimal Discount = 0, DateTimeOffset ReceivedAt = default)
{
    /// <summary>What the receipt still adds to the debt before payments: its total, less its discount and its returns.</summary>
    public decimal Net => Total - Discount - Returned;
}

/// <summary>Money that came back from the supplier: a Recorded payment of kind PaidBack.</summary>
public sealed record DebtMoneyBackRow(Guid SupplierId, DateOnly Date, decimal Amount);

/// <param name="ReceiptCode">Null on a return made without a receipt.</param>
/// <param name="Value">Σ line amounts less the discount of the return.</param>
public sealed record StatementReturnRow(Guid Id, string Code, string? ReceiptCode, DateOnly ReturnDate, decimal Value);

public sealed record StatementPaymentRow(
    Guid Id, string Code, SupplierPaymentKind Kind, DateOnly PaymentDate, decimal Amount, PaymentMethod Method, string? BankReference, string? Note, decimal DiscountAmount = 0);

/// <summary>A Recorded payment of kind Payment; it covers receipts with its amount and its discount.</summary>
public sealed record DebtPaymentRow(
    Guid SupplierId, Guid Id, string Code, DateOnly PaymentDate, decimal Amount, decimal DiscountAmount, PaymentMethod Method, Guid RecordedByAccountId, int StatusId)
{
    public decimal Covers => Amount + DiscountAmount;
}

/// <summary>The part of a Recorded payment of kind Payment its Accountant set against one Completed receipt.</summary>
public sealed record DebtAllocationRow(Guid SupplierId, Guid PaymentId, Guid ReceiptId, decimal Amount);

/// <summary>PURCHASE-04 read side: every figure is summed or grouped in the database, and no graph of receipts is loaded.</summary>
public interface ISupplierDebtQuery
{
    /// <summary>
    /// The sums of one supplier, or of every supplier when <paramref name="supplierId"/> is null, as of <paramref name="asOf"/>
    /// (inclusive). A supplier with no receipt and no payment has no entry.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, SupplierDebtSums>> SumsAsync(Guid? supplierId, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>Those of the suppliers whose name or phone contains <paramref name="search"/>, Discontinued ones included (GBR-01).</summary>
    Task<IReadOnlyList<DebtSupplierRow>> SuppliersAsync(IReadOnlyCollection<Guid> supplierIds, string? search, CancellationToken cancellationToken);

    /// <summary>The Completed receipts of the suppliers, by date received and then code.</summary>
    Task<IReadOnlyList<DebtReceiptRow>> ReceiptsAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken);

    /// <summary>Every amount that came back from the suppliers, by date.</summary>
    Task<IReadOnlyList<DebtMoneyBackRow>> MoneyBackAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken);

    /// <summary>Every Recorded payment of kind Payment to the suppliers.</summary>
    Task<IReadOnlyList<DebtPaymentRow>> PaymentsAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken);

    /// <summary>The parts of the Recorded payments of the suppliers set against their Completed receipts.</summary>
    Task<IReadOnlyList<DebtAllocationRow>> AllocationsAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken);

    /// <summary>What counts in the debt of the supplier inside the period (inclusive): Completed receipts, Recorded returns, Recorded payments.</summary>
    Task<(IReadOnlyList<DebtReceiptRow> Receipts, IReadOnlyList<StatementReturnRow> Returns, IReadOnlyList<StatementPaymentRow> Payments)> StatementRowsAsync(
        Guid supplierId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
