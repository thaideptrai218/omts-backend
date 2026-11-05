using OMTS.Application.Features.AccessManagement.Dtos;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>A supplier on the debt screens, Discontinued ones included (GBR-01).</summary>
public sealed record SupplierDebtSupplier(Guid Id, string Name, string PhoneNumber, int StatusId, string Status);

/// <param name="Debt">Above zero: what the cooperative owes; below zero: what it has paid in advance.</param>
/// <param name="DebtAgeDays">Days since <paramref name="OldestUnpaidDate"/>; null when the debt is not above zero.</param>
/// <param name="OldestUnpaidDate">The date received of the oldest unpaid receipt (or the date of money paid back that no payment covers).</param>
public sealed record SupplierDebtRow(SupplierDebtSupplier Supplier, decimal Debt, int? DebtAgeDays, DateOnly? OldestUnpaidDate);

/// <param name="TotalOwed">Σ of the debts above zero of every supplier that matches, not only of the page.</param>
/// <param name="TotalAdvance">Σ paid in advance (a positive amount), same scope.</param>
/// <param name="Message">MSG-GEN-10 when no supplier is listed (PURCHASE-04.1).</param>
public sealed record SupplierDebtList(Paged<SupplierDebtRow> Page, decimal TotalOwed, decimal TotalAdvance, string? Message, string? MessageCode);

/// <summary>PURCHASE-05 step 1: the debt today and the unpaid receipts, oldest first (SCR-PURCHASE-05).</summary>
public sealed record SupplierDebtDetail(SupplierDebtSupplier Supplier, decimal DebtToday, int? DebtAgeDays, DateOnly? OldestUnpaidDate, IReadOnlyList<UnpaidReceipt> UnpaidReceipts);

/// <summary>PURCHASE-04 step 3: for any supplier, also with a zero debt: debt at the start, entries in date order, debt at the end.</summary>
public sealed record SupplierDebtStatement(
    SupplierDebtSupplier Supplier, DateOnly From, DateOnly To, decimal OpeningDebt, IReadOnlyList<SupplierStatementEntry> Entries,
    decimal ClosingDebt, decimal DebtToday, int? DebtAgeDays);

/// <summary>PURCHASE-04.2: the debt list, and the statement of the supplier when one is chosen.</summary>
public sealed record SupplierDebtExportQuery(Guid? SupplierId = null, DateOnly? From = null, DateOnly? To = null, string? Search = null);
