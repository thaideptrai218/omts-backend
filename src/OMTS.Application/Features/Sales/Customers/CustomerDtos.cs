using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>
/// The customer form. Only the name and the first phone number are required. A save replaces every field: what the request leaves out is cleared (the form always sends all of
/// it), except <paramref name="Code"/>, which empty keeps (an update) or generates (a create).
/// </summary>
/// <param name="PhoneNumber">
/// Required, except on a profile linked to a trader account: there it is locked, so leave it out or send the same number.
/// </param>
/// <param name="Code">The code the user types: 1–30 ASCII letters, digits, "-", "_" or "."; unique regardless of letter case. Empty: the next KH code (a create) or the stored one (an update).</param>
/// <param name="CustomerType">Individual when left out.</param>
/// <param name="BirthDate">Not in the future.</param>
/// <param name="Email">A valid address when entered; staff may edit it also on a profile linked to a trader account.</param>
/// <param name="TaxCode">10 digits with an optional -3 digits branch part, or 12 digits.</param>
/// <param name="IdentityNumber">CCCD, 12 digits. Personal data: never in a list, an export or the audit log.</param>
/// <param name="BankBin">A bank of the bank list; with <paramref name="BankAccountNumber"/>, the account the customer is paid back to.</param>
public sealed record SaveCustomerRequest(
    string? Name,
    string? PhoneNumber,
    string? Address,
    string? Note,
    string? RowVersion = null,
    string? Code = null,
    CustomerType? CustomerType = null,
    Gender? Gender = null,
    DateOnly? BirthDate = null,
    string? PhoneNumber2 = null,
    string? Email = null,
    string? Facebook = null,
    string? Province = null,
    string? Ward = null,
    string? CompanyName = null,
    string? TaxCode = null,
    string? IdentityNumber = null,
    string? BankBin = null,
    string? BankAccountNumber = null);

/// <summary>The customer list and export query. The figures (debt, sales) are null, and their sort and filter parameters refused, without CUSTOMER_DEBTS View.</summary>
/// <param name="Search">Text in the name, code or phone number.</param>
/// <param name="Status">InUse (the default, what the pickers offer), Discontinued or All.</param>
/// <param name="BirthdayFrom">With BirthdayTo: customers whose birthday (month and day) is in the range.</param>
/// <param name="LastTransactionFrom">Business dates of the last sale.</param>
/// <param name="TotalSalesFrom">With SalesFrom and SalesTo: the sales of that period.</param>
/// <param name="CreatedFrom">Business dates of the creation.</param>
/// <param name="SortBy">code (the default, newest first), name, phone, type, province, createdAt, status, totalSales, netSales, currentDebt or lastTransaction.</param>
/// <param name="SortDir">asc or desc; the default is desc for the code and asc for the others.</param>
public sealed record CustomerListQuery(
    string? Search = null,
    CustomerType? Type = null,
    Gender? Gender = null,
    DateOnly? BirthdayFrom = null,
    DateOnly? BirthdayTo = null,
    DateOnly? LastTransactionFrom = null,
    DateOnly? LastTransactionTo = null,
    decimal? TotalSalesFrom = null,
    decimal? TotalSalesTo = null,
    DateOnly? SalesFrom = null,
    DateOnly? SalesTo = null,
    decimal? CurrentDebtFrom = null,
    decimal? CurrentDebtTo = null,
    string? Province = null,
    CustomerStatusFilter Status = CustomerStatusFilter.InUse,
    Guid? CreatedById = null,
    DateOnly? CreatedFrom = null,
    DateOnly? CreatedTo = null,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 20);

public enum CustomerStatusFilter
{
    InUse,
    Discontinued,
    All,
}

/// <param name="HasTraderAccount">Linked to a trader account.</param>
/// <param name="PhotoUrl">A temporary address of the photo; null without one.</param>
/// <param name="CurrentDebt">Null without CUSTOMER_DEBTS View (BR-FARM-07). Below 0 it is money owed back.</param>
/// <param name="TotalSales">Null without CUSTOMER_DEBTS View: the invoices not cancelled in the period, before their price cuts.</param>
/// <param name="NetSales">Null without CUSTOMER_DEBTS View: the same, after the price cuts.</param>
/// <param name="LastTransactionAt">The time of the last invoice that is not cancelled.</param>
public sealed record CustomerListItem(
    Guid Id,
    string Code,
    string Name,
    string? PhoneNumber,
    string? Address,
    bool HasTraderAccount,
    string RowVersion,
    int StatusId,
    string Status,
    CustomerType CustomerType,
    Gender? Gender,
    DateOnly? BirthDate,
    string? PhoneNumber2,
    string? Email,
    string? Facebook,
    string? Province,
    string? Ward,
    string? CompanyName,
    string? TaxCode,
    string? Note,
    string? PhotoUrl,
    DateTimeOffset? LastTransactionAt,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    decimal? CurrentDebt,
    decimal? TotalSales,
    decimal? NetSales);

/// <summary>The sums over every customer that matches the filters, not only the page. Null without CUSTOMER_DEBTS View.</summary>
/// <param name="TotalSales">Σ the sales before the price cuts.</param>
/// <param name="NetSales">Σ the sales after the price cuts.</param>
public sealed record CustomerListSummary(decimal Debt, decimal TotalSales, decimal NetSales);

/// <summary>The page has the shape of every paged list (<c>items</c>, <c>page</c>, <c>pageSize</c>, <c>total</c>) and the summary row.</summary>
public sealed record CustomerListPage(IReadOnlyList<CustomerListItem> Items, int Page, int PageSize, int Total, CustomerListSummary? Summary);

public sealed record LinkedTraderAccount(Guid AccountId, string? Username, string DisplayName);

/// <param name="PhoneLocked">Linked to a trader account: the phone number follows the account (BR-SALE-04).</param>
/// <param name="MergedIntoCustomerId">Set on a merged profile: it is read-only and no longer offered.</param>
/// <param name="CurrentDebt">BR-DEBT-02, below 0 money owed back. Unlike the list, the detail gives it to every caller: the invoice screen of the Zone Manager shows it.</param>
/// <param name="IsUsed">An invoice, payment, debt adjustment or purchase request names it, or it has an account: it cannot be deleted, only discontinued.</param>
/// <param name="IdentityNumber">Personal data: on this detail only.</param>
/// <param name="PhotoUrl">A temporary address of the photo; null without one.</param>
public sealed record CustomerView(
    Guid Id,
    string Name,
    string? PhoneNumber,
    string? Email,
    string? Address,
    string? Note,
    bool PhoneLocked,
    LinkedTraderAccount? TraderAccount,
    Guid? MergedIntoCustomerId,
    decimal CurrentDebt,
    string RowVersion,
    string Code,
    int StatusId,
    string Status,
    CustomerType CustomerType,
    Gender? Gender,
    DateOnly? BirthDate,
    string? PhoneNumber2,
    string? Facebook,
    string? Province,
    string? Ward,
    string? CompanyName,
    string? TaxCode,
    string? IdentityNumber,
    string? BankBin,
    string? BankName,
    string? BankAccountNumber,
    string? PhotoUrl,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    bool IsUsed);

/// <summary>A saved profile with MSG-GEN-05 (SALE-04 step 7).</summary>
public sealed record CustomerSaved(CustomerView Customer, string Message, string MessageCode);

public enum CustomerBulkAction
{
    Discontinue,
    Resume,
    Delete,
}

public sealed record CustomerBulkRequest(IReadOnlyList<Guid>? Ids, CustomerBulkAction? Action);

/// <param name="Code">The message code of a refusal; null when it succeeded.</param>
public sealed record CustomerBulkOutcome(Guid Id, bool Succeeded, string? Code, string? Message);

public sealed record CustomerBulkResult(int Succeeded, int Failed, IReadOnlyList<CustomerBulkOutcome> Outcomes);

/// <param name="Row">The row number in the file.</param>
/// <param name="Fields">The invalid columns.</param>
public sealed record CustomerImportError(int Row, IReadOnlyList<string> Fields, string Message);

/// <summary>All-or-nothing: <paramref name="Imported"/> is zero whenever <paramref name="Errors"/> has a row.</summary>
public sealed record CustomerImportResult(int Imported, IReadOnlyList<CustomerImportError> Errors);

/// <summary>Merge the profile <paramref name="MergedCustomerId"/> into the kept profile of the route (SALE-04.3).</summary>
/// <param name="OwnerApproved">The Zone Manager confirms that the Cooperative Owner agreed (MSG-SALE-04); the Owner need not send it.</param>
public sealed record MergeCustomerRequest(Guid? MergedCustomerId, string? KeptRowVersion, string? MergedRowVersion, bool OwnerApproved = false);

/// <param name="HasTraderAccount">Linked to a trader account.</param>
public sealed record CustomerMergeParty(Guid Id, string Name, string? PhoneNumber, decimal CurrentDebt, bool HasTraderAccount, string RowVersion, string? Code = null);

/// <summary>What SALE-04.3 step 3 shows before the merge.</summary>
/// <param name="CountedWithKept">The rows of the merged profile that move to the kept one.</param>
/// <param name="RequiresOwnerApproval">True for a Zone Manager, who answers <see cref="Confirmation"/>; the Owner only confirms (MSG-GEN-04).</param>
/// <param name="Confirmation">The text of MSG-SALE-04 for a Zone Manager; null for the Owner.</param>
/// <param name="LocksMergedAccount">The merged profile has a trader account: after the merge it is locked and cannot sign in (the kept profile keeps its own).</param>
public sealed record CustomerMergePreview(
    CustomerMergeParty Kept,
    CustomerMergeParty Merged,
    IReadOnlyList<CustomerReferenceCount> CountedWithKept,
    bool RequiresOwnerApproval,
    string? Confirmation,
    bool LocksMergedAccount = false);
