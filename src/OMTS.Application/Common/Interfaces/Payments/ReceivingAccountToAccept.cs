namespace OMTS.Application.Common.Interfaces.Payments;

/// <summary>A checked and normalised receiving bank account (BR-DEBT-01).</summary>
/// <param name="BankName">The short name in the list of banks.</param>
public sealed record ReceivingAccountToAccept(string BankBin, string BankName, string AccountNumber, string AccountHolderName);
