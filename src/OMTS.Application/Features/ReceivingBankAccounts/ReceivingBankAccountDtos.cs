namespace OMTS.Application.Features.ReceivingBankAccounts;

/// <summary>One bank of the list of banks.</summary>
public sealed record BankView(string Bin, string ShortName, string Name);

/// <param name="BankName">The short name of the bank.</param>
/// <param name="AcceptedAt">When the Payment Notification Service accepted the account.</param>
public sealed record ReceivingBankAccountDetails(string BankBin, string BankName, string AccountNumber, string AccountHolderName, DateTimeOffset AcceptedAt);

/// <param name="Account">Null when none is set yet.</param>
/// <param name="RowVersion">The version of the cooperative record, also when no account is set.</param>
public sealed record ReceivingBankAccountView(ReceivingBankAccountDetails? Account, string RowVersion);

/// <param name="ConfirmAccount">The answer Yes to MSG-DEBT-01. Without it nothing is saved and the API asks the question.</param>
public sealed record SaveReceivingBankAccountRequest(
    string? BankBin,
    string? AccountNumber,
    string? AccountHolderName,
    bool ConfirmAccount,
    string? RowVersion);

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record ReceivingBankAccountSaved(ReceivingBankAccountView ReceivingBankAccount, string Message, string MessageCode);
