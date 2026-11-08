using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The customer payments of DEBT-02 (BR-DEBT-03), with their customer profile and the invoice they came with.</summary>
public interface ICustomerPaymentRepository
{
    void Add(CustomerPayment payment);

    /// <summary>Tracked, with its customer and invoice.</summary>
    Task<CustomerPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Tracked, with their customers and invoices; the ids not found are left out.</summary>
    Task<IReadOnlyList<CustomerPayment>> GetForEditAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken);

    /// <summary>Read only, with its customer and invoice.</summary>
    Task<CustomerPayment?> GetAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<CustomerPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>The latest payment that is not cancelled with this bank reference, whatever its customer (MSG-DEBT-05), with its customer.</summary>
    Task<CustomerPayment?> FindNotCancelledByBankReferenceAsync(string bankReference, CancellationToken cancellationToken);

    /// <summary>The transfer already recorded with the VietQR code of an invoice under this bank reference, cancelled or not (GFR-08), read only.</summary>
    Task<CustomerPayment?> FindVietQrByBankReferenceAsync(string bankReference, CancellationToken cancellationToken);

    /// <summary>Σ of the transfers recorded with this invoice code and no invoice (GFR-08) that are not cancelled: a transfer short of a sale awaiting it.</summary>
    Task<decimal> ReceivedWithoutInvoiceAsync(string invoiceCode, CancellationToken cancellationToken);

    /// <summary>The payments Awaiting confirmation, oldest first (payment date, then code), and their number and sum.</summary>
    Task<(IReadOnlyList<CustomerPayment> Items, int Total, decimal TotalAmount)> AwaitingConfirmationAsync(int page, int pageSize, CancellationToken cancellationToken);
}
