namespace OMTS.Domain.Enums;

/// <summary>How a customer paid (BR-DEBT-03): with the VietQR code of an invoice, by another transfer, or in cash.</summary>
public enum CustomerPaymentMethod
{
    VietQrInvoice,
    OtherTransfer,
    Cash,

    /// <summary>Into an e-wallet account of the cash book; only a cash book voucher that counts in the debt makes one.</summary>
    EWallet,
}
