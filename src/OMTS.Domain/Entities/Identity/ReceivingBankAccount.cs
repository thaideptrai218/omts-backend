namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// DEBT-01, BR-DEBT-01: the one bank account of the cooperative that receives the transfers made with the VietQR codes
/// of the sales invoices, as the Payment Notification Service accepted it. Owned by <see cref="Cooperative"/>. Never
/// shown on the provenance page (BR-ORIGIN-04).
/// </summary>
public class ReceivingBankAccount
{
    /// <summary>The NAPAS bank identification number (6 digits) that goes into the VietQR code.</summary>
    public string BankBin { get; set; } = string.Empty;

    /// <summary>The short name of the bank in the list of banks, at the time of the save.</summary>
    public string BankName { get; set; } = string.Empty;

    public string AccountNumber { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;

    /// <summary>When the Payment Notification Service accepted the account.</summary>
    public DateTimeOffset AcceptedAt { get; set; }
}
