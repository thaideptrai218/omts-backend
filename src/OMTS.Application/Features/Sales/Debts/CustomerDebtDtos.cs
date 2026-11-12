using OMTS.Application.Features.AccessManagement.Dtos;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>A customer profile on the debt screens (SCR-DEBT-02).</summary>
/// <param name="Code">The customer code (KH000123): links to the customer screen search by it, never by name.</param>
public sealed record CustomerDebtCustomer(Guid Id, string Name, string? PhoneNumber, string? Code = null);

/// <param name="Debt">Above 0: what the customer owes; below 0: money owed back to the customer (BR-DEBT-02).</param>
/// <param name="DebtAgeDays">Days since <paramref name="OldestUnpaidDate"/>; null when the debt is not above 0 (BR-DEBT-05).</param>
/// <param name="OldestUnpaidDate">The invoice date of the oldest unpaid invoice.</param>
public sealed record CustomerDebtRow(CustomerDebtCustomer Customer, decimal Debt, int? DebtAgeDays, DateOnly? OldestUnpaidDate);

/// <summary>DEBT-03 step 1.</summary>
/// <param name="ReceivablesTotal">Σ of the debts above 0 of every customer that matches, not only of the page.</param>
/// <param name="OwedBackTotal">Σ money owed back (a positive amount), same scope.</param>
/// <param name="AwaitingConfirmationCount">Customer payments Awaiting confirmation, cooperative-wide.</param>
/// <param name="Message">MSG-GEN-11 when no customer has a debt (DEBT-03.1); MSG-GEN-10 when a search finds none.</param>
public sealed record CustomerDebtList(
    Paged<CustomerDebtRow> Page, decimal ReceivablesTotal, decimal OwedBackTotal, int AwaitingConfirmationCount, string? Message, string? MessageCode);

/// <summary>DEBT-03 step 3: for any profile, also with a zero debt.</summary>
/// <param name="DebtToday">The debt now, also outside the period.</param>
/// <param name="UnpaidInvoices">Today, oldest first (BR-DEBT-05).</param>
public sealed record CustomerDebtStatement(
    CustomerDebtCustomer Customer, DateOnly From, DateOnly To, decimal OpeningDebt, IReadOnlyList<CustomerStatementEntry> Entries, decimal ClosingDebt,
    decimal DebtToday, int? DebtAgeDays, IReadOnlyList<UnpaidInvoice> UnpaidInvoices);

/// <summary>The debt tab of a customer: all its entries, newest first, or the entries of some kinds.</summary>
/// <param name="From">Business dates, inclusive; the whole history when neither is given.</param>
/// <param name="Kinds">Only these kinds of entries (repeat the parameter); every kind when empty. The debt after each entry is that of the whole history, whatever the filter.</param>
public sealed record CustomerDebtTabQuery(DateOnly? From = null, DateOnly? To = null, IReadOnlyList<CustomerStatementEntryKind>? Kinds = null, int Page = 1, int PageSize = 20);

/// <summary>The debt tab of a customer (customer screen): the debt now, the unpaid invoices oldest first and the entries, each with the debt after it.</summary>
/// <param name="Debt">Above 0: what the customer owes; below 0: money owed back (BR-DEBT-02).</param>
/// <param name="Entries">Invoices, price cuts, payments, discounts, money paid back and adjustments, newest first; cancelled ones and payments waiting for their transfer are listed and count 0.</param>
/// <param name="OpeningDebt">The debt before the first entry of the period (0 without one).</param>
/// <param name="AwaitingConfirmationCount">Payments of this customer Awaiting confirmation (cash at the port).</param>
/// <param name="PendingTransferCount">Payments of this customer waiting for their transfer (VietQR code made).</param>
public sealed record CustomerDebtTab(
    CustomerDebtCustomer Customer, decimal Debt, int? DebtAgeDays, IReadOnlyList<UnpaidInvoice> UnpaidInvoices, Paged<CustomerStatementEntry> Entries, decimal OpeningDebt,
    int AwaitingConfirmationCount, int PendingTransferCount);

/// <summary>DEBT-03.2: the debt list, and the statement of the customer when one is chosen.</summary>
public sealed record CustomerDebtExportQuery(Guid? CustomerId = null, DateOnly? From = null, DateOnly? To = null, string? Search = null);
