namespace OMTS.Application.Features.Sales.Customers;

/// <summary>Optional fields of a customer profile that the cooperative makes required; the name and the first phone number always are, so they are not here.</summary>
/// <param name="Address">The address line, the province and the ward are all required.</param>
/// <param name="Bank">The bank and the account number.</param>
public sealed record CustomerRequiredFields(
    bool PhoneNumber2, bool Email, bool Gender, bool BirthDate, bool Address, bool Facebook, bool Bank, bool TaxCode);

/// <param name="DebtWarningEnabled">Customers over the limit are flagged on the list and the invoice form.</param>
/// <param name="MaxDebtAmount">VND; null: the amount is not a limit.</param>
/// <param name="MaxDebtDays">Days since the oldest unpaid debt invoice; null: the days are not a limit.</param>
/// <param name="AllowMoreDebtOverLimit">True: only a warning. False: an invoice that leaves a debt for a customer over the limit is refused.</param>
/// <param name="NoDebtForWalkIn">A walk-in invoice must be paid in full.</param>
public sealed record CustomerSettingsView(
    bool DebtWarningEnabled,
    decimal? MaxDebtAmount,
    int? MaxDebtDays,
    bool AllowMoreDebtOverLimit,
    bool NoDebtForWalkIn,
    CustomerRequiredFields RequiredFields,
    string RowVersion);

/// <summary>Saves every value; the name and the first phone number stay required whatever is sent.</summary>
public sealed record UpdateCustomerSettingsRequest(
    bool? DebtWarningEnabled,
    decimal? MaxDebtAmount,
    int? MaxDebtDays,
    bool? AllowMoreDebtOverLimit,
    bool? NoDebtForWalkIn,
    CustomerRequiredFields? RequiredFields,
    string? RowVersion);
