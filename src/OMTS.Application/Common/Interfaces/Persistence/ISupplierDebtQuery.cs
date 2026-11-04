using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// One supplier's sums as of a date (BR-PURCHASE-04), computed by SQL over Completed receipts, their returns and Recorded
/// payments. Draft and Cancelled receipts and Cancelled payments never count, also in past periods.
/// </summary>
/// <param name="Receipts">Σ totals of the Completed receipts received by the date.</param>
/// <param name="Returned">Σ values of their purchase returns dated by the date.</param>
/// <param name="Refunded">Σ refunds on those returns: the money back of a return until P4 turns it into money paid back.</param>
/// <param name="Paid">Σ Recorded payments of kind Payment dated by the date.</param>
/// <param name="PaidBack">Σ Recorded payments of kind PaidBack dated by the date.</param>
public sealed record SupplierDebtSums(decimal Receipts, decimal Returned, decimal Refunded, decimal Paid, decimal PaidBack)
{
    public static readonly SupplierDebtSums Zero = new(0, 0, 0, 0, 0);

    /// <summary>What the cooperative owes; below zero when it has paid in advance.</summary>
    public decimal Debt => Receipts - Returned + Refunded - Paid + PaidBack;

    /// <summary>The money the supplier holds, available to cover receipts (BR-PURCHASE-04); below zero when it paid back more than it was paid.</summary>
    public decimal Credit => Paid - PaidBack - Refunded;
}

/// <summary>A supplier that may be listed on the debt list.</summary>
public sealed record DebtSupplierRow(Guid Id, string Name, string PhoneNumber, int StatusId);

/// <summary>A Completed receipt with its total and the value of all its returns.</summary>
public sealed record DebtReceiptRow(Guid SupplierId, Guid Id, string Code, DateOnly ReceiptDate, decimal Total, decimal Returned);

/// <summary>Money that came back from the supplier: a Recorded payment of kind PaidBack, or a refund on a return until P4.</summary>
public sealed record DebtMoneyBackRow(Guid SupplierId, DateOnly Date, decimal Amount);

public sealed record StatementReturnRow(Guid Id, string Code, string ReceiptCode, DateOnly ReturnDate, decimal Value, decimal Refund);

public sealed record StatementPaymentRow(
    Guid Id, string Code, SupplierPaymentKind Kind, DateOnly PaymentDate, decimal Amount, PaymentMethod Method, string? BankReference, string? Note);

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

    /// <summary>What counts in the debt of the supplier inside the period (inclusive): Completed receipts, their returns, Recorded payments.</summary>
    Task<(IReadOnlyList<DebtReceiptRow> Receipts, IReadOnlyList<StatementReturnRow> Returns, IReadOnlyList<StatementPaymentRow> Payments)> StatementRowsAsync(
        Guid supplierId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
