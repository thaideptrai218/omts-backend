using OMTS.Application.Features.AccessManagement.Dtos;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>A customer profile on the debt screens (SCR-DEBT-02).</summary>
public sealed record CustomerDebtCustomer(Guid Id, string Name, string? PhoneNumber);

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

/// <summary>DEBT-03.2: the debt list, and the statement of the customer when one is chosen.</summary>
public sealed record CustomerDebtExportQuery(Guid? CustomerId = null, DateOnly? From = null, DateOnly? To = null, string? Search = null);
