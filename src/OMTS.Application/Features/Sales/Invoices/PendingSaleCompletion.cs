using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-05 step 8 (GFR-08): the transfer of the VietQR code arrived, so the sale awaiting it is saved as Completed, Pay now, with its code and lines at the prices shown, its cash
/// part (SALE-05.7 step 3) and the lots whose mark is kept closed, as the Zone Manager who sold it; the Owners get MSG-SALE-08. The transfer itself is recorded by the caller.
/// Everything goes in the caller's unit of work.
/// </summary>
public sealed class PendingSaleCompletion(
    ICustomerRepository customers,
    IPurchaseRequestRepository requests,
    IProductRepository products,
    IHarvestLotRepository lots,
    SalesInvoiceWriter writer,
    PendingSaleWriter pendingWriter,
    SalesInvoiceNotifications notifications,
    IDocumentNumberAllocator numbers,
    IUnitOfWork unitOfWork)
{
    // A profile merged into one that was merged again: follow a few steps, never a loop.
    private const int MaxMergeSteps = 10;

    public async Task<SalesInvoice> CompleteAsync(PendingSale sale, CancellationToken cancellationToken)
    {
        var snapshot = PendingSaleSnapshot.Read(sale);
        var (read, invalid) = SalesInvoiceEntryReader.Read(snapshot.Request);
        if (invalid.Count > 0)
        {
            throw new InvalidOperationException($"Pending sale {sale.Code} holds a request that is no longer valid: {string.Join(", ", invalid)}.");
        }

        var entries = read!;
        var customer = await CustomerAsync(sale.CustomerId, cancellationToken);
        var request = entries.RequestId is { } requestId ? await requests.GetForEditAsync(requestId, cancellationToken) : null;
        if (request is not null && (request.StatusId != StatusIds.PurchaseRequest.Accepted || request.CustomerId != customer?.Id))
        {
            // The trader cancelled the request while the transfer was awaited: the invoice no longer takes it.
            request = null;
            entries = entries with { RequestId = null, TakesRequestInFull = false };
        }

        var lineProducts = new Dictionary<Guid, Product>();
        foreach (var productId in entries.Lines.Select(l => l.ProductId).Distinct())
        {
            lineProducts[productId] = await products.GetForEditAsync(productId, cancellationToken)
                ?? throw new InvalidOperationException($"Product {productId} of pending sale {sale.Code} no longer exists.");
        }

        var byId = (await lots.ListForInvoiceAsync(entries.LotIds, cancellationToken)).ToDictionary(l => l.Id);
        var lineLots = entries.LotIds.Select(id => byId[id]).ToList();
        // A parallel close or sale of one of these lots makes one of the two saves fail, as at the port.
        lineLots.ForEach(unitOfWork.MarkChanged);

        var prices = snapshot.Lines.ToDictionary(l => l.LineNumber);
        var lines = entries.Lines.Select(e =>
        {
            var price = prices[e.LineNumber];
            return new PricedLine(e, lineProducts[e.ProductId], byId[e.LotId], price.UnitPrice, price.PriceSource, price.Amount, price.ListPrice);
        }).ToList();
        var decision = new SalesInvoiceDecision(
            lines, snapshot.Subtotal, snapshot.Total, new PortCashSplit(sale.CashAmount, 0m, 0m, sale.ToPayAmount), SalesPaymentTerm.PayNow, 0m, sale.ToPayAmount);
        var facts = new SalesInvoiceFacts(customer, request, lineProducts, lineLots, sale.ZoneId, snapshot.ByOwner, 0m);

        var cashCode = sale.CashAmount > 0m ? SalesDocumentCodes.PaymentCode(await numbers.NextAsync(DocumentSequenceKeys.CustomerPayment, cancellationToken)) : null;
        var invoice = writer.Write(entries, facts, decision, sale.Code, cashCode, deduplicationKey: null, sellerOnTransfer: sale.CreatedByAccountId);
        pendingWriter.End(sale, PendingSaleStatus.Completed, invoice.Id, by: null);
        await notifications.SaleAsync(invoice, decision, sale.CreatedByAccountId, cancellationToken);
        return invoice;
    }

    /// <summary>The profile of the sale, or the one it was merged into while the transfer was awaited (SALE-04.3 moves invoices, not pending sales).</summary>
    private async Task<Customer?> CustomerAsync(Guid? customerId, CancellationToken cancellationToken)
    {
        if (customerId is not { } id)
        {
            return null;
        }

        var customer = await customers.GetForEditAsync(id, cancellationToken) ?? throw new InvalidOperationException($"Customer {id} of a pending sale no longer exists.");
        for (var step = 0; customer.MergedIntoCustomerId is { } kept && step < MaxMergeSteps; step++)
        {
            customer = await customers.GetForEditAsync(kept, cancellationToken) ?? customer;
        }

        return customer;
    }
}
