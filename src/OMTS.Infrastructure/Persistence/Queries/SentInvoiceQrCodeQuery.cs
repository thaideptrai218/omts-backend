using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// Sales invoices and their messages do not exist yet, so no VietQR code has been sent. The invoice PRs (SALE-05, SALE-06)
/// replace this with the real count; the message of DEBT-01 already asks for it.
/// </summary>
internal sealed class SentInvoiceQrCodeQuery : ISentInvoiceQrCodeQuery
{
    public Task<int> CountUnpaidInvoicesWithSentCodesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}
