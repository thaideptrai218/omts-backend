namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The change impact of the receiving bank account, for {old_codes_text} of MSG-DEBT-01 (BR-DEBT-01).</summary>
public interface ISentInvoiceQrCodeQuery
{
    /// <summary>How many unpaid invoices have VietQR codes already sent for the current account.</summary>
    Task<int> CountUnpaidInvoicesWithSentCodesAsync(CancellationToken cancellationToken);
}
