namespace OMTS.Application.Common.Models.Payments;

/// <summary>The bank account linked to the payOS channel, the one payOS links pay into; a Bank account of the cash book with the same bank and number records the payments.</summary>
/// <param name="BankBin">The bank's BIN (6 digits).</param>
public sealed record PayOsAccount(string BankBin, string AccountNumber);
