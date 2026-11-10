namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>
/// Whether an invoice has a sales return that is not cancelled (BR-CLAIM-01, BR-CLAIM-02). The sales returns come with CLAIM-03 (PR 13), which replaces the implementation that answers
/// "no" with the real query; CLAIM-01 and CLAIM-02 already ask it, so they do not change then.
/// </summary>
public interface IInvoiceSalesReturnCheck
{
    Task<bool> HasSalesReturnAsync(Guid invoiceId, CancellationToken cancellationToken);
}
