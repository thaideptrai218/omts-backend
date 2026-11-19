namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The codes of the money lines the financial report reads; the calculator signs them (deductions and costs lower the result).</summary>
public static class FinancialEntryCodes
{
    public const string Revenue = "revenue";
    public const string InvoiceDiscounts = "invoice-discounts";
    public const string PriceCuts = "price-cuts";
    public const string SalesReturns = "sales-returns";
    public const string PaymentDiscounts = "payment-discounts";
    public const string InputUseCost = "input-use-cost";
    public const string InputDisposalCost = "input-disposal-cost";
    public const string LaborCost = "labor-cost";
    public const string OtherExpense = "other-expense";
    public const string OtherIncome = "other-income";
}

/// <summary>An amount (whole VND, never negative) of one <see cref="FinancialEntryCodes"/> line on a business date (UTC+7). A payroll sits on the first day of its expense month.</summary>
public sealed record FinancialEntry(string Code, DateOnly Date, decimal Amount);

/// <summary>The money of the business result of a period, from the records themselves. Read only.</summary>
public interface IFinancialReportQuery
{
    /// <summary>
    /// Invoices (Completed, by invoice date): the subtotal plus other charges, the discounts, the price cuts and the sales returns that stand (by the date of their invoice, as the sales
    /// report counts them); customer payment discounts (by payment date); Completed input issues (by issue date); Finalized payrolls (net pay, by expense month, whole months from the month of
    /// <paramref name="from"/> to that of <paramref name="to"/>); cash vouchers that count in the result, not cancelled (by voucher date).
    /// </summary>
    Task<IReadOnlyList<FinancialEntry>> EntriesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
