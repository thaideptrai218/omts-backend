namespace OMTS.Application.Features.Sales.Debts;

/// <summary>The four sums of the customer debt formula (BR-DEBT-02), each over the records that are not cancelled.</summary>
/// <param name="Invoices">The totals of the customer's sales invoices, at their latest values.</param>
/// <param name="Returns">The values of their sales returns (CLAIM-03; 0 until that PR).</param>
/// <param name="Payments">The payments received, also those Awaiting confirmation.</param>
/// <param name="PaidBack">The money paid back to the customer.</param>
public sealed record CustomerDebtFigures(decimal Invoices, decimal Returns, decimal Payments, decimal PaidBack)
{
    public static readonly CustomerDebtFigures None = new(0m, 0m, 0m, 0m);
}

/// <summary>The customer debt (BR-DEBT-02), worked out each time it is shown and never stored, as pure functions so they are unit tested and give one answer everywhere.</summary>
public static class CustomerDebtMath
{
    /// <summary>customer debt = invoices − sales returns − payments received + money paid back. Below 0 it is money owed back to the customer.</summary>
    public static decimal Debt(CustomerDebtFigures figures) =>
        figures.Invoices - figures.Returns - figures.Payments + figures.PaidBack;

    /// <summary>What the customer still owes: the debt when above 0 (it counts in the receivables), else 0.</summary>
    public static decimal Receivable(decimal debt) => Math.Max(0m, debt);

    /// <summary>What the cooperative owes the customer: the debt below 0, as a positive amount, else 0.</summary>
    public static decimal OwedBack(decimal debt) => Math.Max(0m, -debt);

    /// <summary>The debt after a new invoice and the payments recorded with it (SALE-05): the invoice adds its total, the payments take off what they bring.</summary>
    public static decimal AfterInvoice(decimal debtBefore, decimal invoiceTotal, decimal paidWithInvoice) => debtBefore + invoiceTotal - paidWithInvoice;
}
