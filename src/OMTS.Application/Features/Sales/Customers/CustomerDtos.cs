using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Sales.Customers;

/// <param name="PhoneNumber">
/// Required, except on a profile linked to a trader account: there it is locked, so leave it out or send the same number.
/// </param>
public sealed record SaveCustomerRequest(string? Name, string? PhoneNumber, string? Address, string? Note, string? RowVersion = null);

public sealed record CustomerListItem(Guid Id, string Name, string? PhoneNumber, string? Address, bool HasTraderAccount, string RowVersion);

public sealed record LinkedTraderAccount(Guid AccountId, string? Username, string DisplayName);

/// <param name="PhoneLocked">Linked to a trader account: the phone number follows the account (BR-SALE-04).</param>
/// <param name="MergedIntoCustomerId">Set on a merged profile: it is read-only and no longer offered.</param>
/// <param name="CurrentDebt">BR-DEBT-02; 0 until invoices and payments exist.</param>
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
    string RowVersion);

/// <summary>A saved profile with MSG-GEN-05 (SALE-04 step 7).</summary>
public sealed record CustomerSaved(CustomerView Customer, string Message, string MessageCode);

/// <summary>Merge the profile <paramref name="MergedCustomerId"/> into the kept profile of the route (SALE-04.3).</summary>
/// <param name="OwnerApproved">The Zone Manager confirms that the Cooperative Owner agreed (MSG-SALE-04); the Owner need not send it.</param>
public sealed record MergeCustomerRequest(Guid? MergedCustomerId, string? KeptRowVersion, string? MergedRowVersion, bool OwnerApproved = false);

public sealed record CustomerMergeParty(Guid Id, string Name, string? PhoneNumber, decimal CurrentDebt, bool HasTraderAccount, string RowVersion);

/// <summary>What SALE-04.3 step 3 shows before the merge.</summary>
/// <param name="CountedWithKept">The rows of the merged profile that move to the kept one.</param>
/// <param name="RequiresOwnerApproval">True for a Zone Manager, who answers <see cref="Confirmation"/>; the Owner only confirms (MSG-GEN-04).</param>
/// <param name="Confirmation">The text of MSG-SALE-04 for a Zone Manager; null for the Owner.</param>
public sealed record CustomerMergePreview(
    CustomerMergeParty Kept,
    CustomerMergeParty Merged,
    IReadOnlyList<CustomerReferenceCount> CountedWithKept,
    bool RequiresOwnerApproval,
    string? Confirmation);
