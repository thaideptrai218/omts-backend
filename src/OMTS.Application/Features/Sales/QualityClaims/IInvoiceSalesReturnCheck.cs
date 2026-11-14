namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>
/// Whether an invoice has a sales return that is not cancelled (BR-CLAIM-01, BR-CLAIM-02), by the rule <c>SalesReturn.Standing</c>: the same one as
/// <c>SalesInvoiceAllowedActions.HasSalesReturn</c>, for callers that hold no loaded invoice.
/// </summary>
public interface IInvoiceSalesReturnCheck
{
    Task<bool> HasSalesReturnAsync(Guid invoiceId, CancellationToken cancellationToken);
}
