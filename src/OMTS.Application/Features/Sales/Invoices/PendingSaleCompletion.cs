using OMTS.Application.Features.Sales.Payments;
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
    ISalesOrderRepository orders,
    IPurchaseRequestRepository requests,
    IProductRepository products,
    IHarvestLotRepository lots,
    SalesInvoiceWriter writer,
    PendingSaleWriter pendingWriter,
    SalesInvoiceNotifications notifications,
    CustomerPaymentCodeService paymentCodes,
    IUnitOfWork unitOfWork)
{
    // A profile merged into one that was merged again: follow a few steps, never a loop.
    private const int MaxMergeSteps = 10;

    public async Task<SalesInvoice> CompleteAsync(PendingSale sale, CancellationToken cancellationToken)
    {
        var snapshot = PendingSaleSnapshot.Read(sale);
        // A sale that was awaiting its transfer before invoices came from orders names no order, and is saved with none.
        var (read, invalid) = SalesInvoiceEntryReader.Read(snapshot.Request, requireOrder: false);
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
                ?? throw new PendingSaleNotSavableException($"product {productId} no longer exists.");
        }

        var byId = (await lots.ListForInvoiceAsync(entries.LotIds, cancellationToken)).ToDictionary(l => l.Id);
        if (entries.LotIds.FirstOrDefault(id => !byId.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            // HARVEST-01.1 refuses to remove a lot of a sale awaiting its transfer; one removed before that rule, or by hand, still never makes the transfer fail.
            throw new PendingSaleNotSavableException($"harvest lot {missing} no longer exists.");
        }

        var lineLots = entries.LotIds.Select(id => byId[id]).ToList();
        // A parallel close or sale of one of these lots makes one of the two saves fail, as at the port.
        lineLots.ForEach(unitOfWork.MarkChanged);

        // The order is read as it is now: the sale is saved whatever its status has become, since the money has arrived (an order that was ended meanwhile is noted in the audit log).
        var orderFacts = await OrderFactsAsync(entries.SalesOrderId, cancellationToken);
        var resolved = SalesInvoiceLineResolver.Resolve(entries.Lines, lineProducts, orderFacts?.Order, []);
        if (!resolved.IsSuccess)
        {
            throw new PendingSaleNotSavableException($"a line no longer matches its product or its order: {resolved.Error!.Message.Text}");
        }

        var prices = snapshot.Lines.ToDictionary(l => l.LineNumber);
        var lines = entries.Lines.Select(e =>
        {
            var price = prices[e.LineNumber];
            return new PricedLine(e, lineProducts[e.ProductId], byId[e.LotId], resolved.Value![e.LineNumber], price.UnitPrice, price.PriceSource, price.Amount, price.ListPrice);
        }).ToList();
        // What the advance of the order covered when the code was shown: the total less the cash and the transfer.
        var advance = customer is null || orderFacts is null ? 0m : Math.Min(orderFacts.AdvanceLeft, Math.Max(0m, snapshot.Total - snapshot.CashRecorded - sale.ToPayAmount));
        var decision = new SalesInvoiceDecision(
            lines, snapshot.Subtotal, snapshot.Total, new PortCashSplit(sale.CashAmount, 0m, 0m, sale.ToPayAmount), SalesPaymentTerm.PayNow, 0m, sale.ToPayAmount, advance);
        var facts = new SalesInvoiceFacts(customer, request, lineProducts, lineLots, sale.ZoneId, snapshot.ByOwner, 0m, resolved.Value!, orderFacts);

        var cashCode = sale.CashAmount > 0m ? await paymentCodes.NextAsync(cancellationToken) : null;
        var invoice = writer.Write(entries, facts, decision, sale.Code, cashCode, deduplicationKey: null, sellerOnTransfer: sale.CreatedByAccountId);
        pendingWriter.End(sale, PendingSaleStatus.Completed, invoice.Id, by: null);
        await notifications.SaleAsync(invoice, decision, sale.CreatedByAccountId, cancellationToken);
        return invoice;
    }

    private async Task<SalesInvoiceOrderFacts?> OrderFactsAsync(Guid? orderId, CancellationToken cancellationToken)
    {
        if (orderId is not { } id)
        {
            return null;
        }

        var order = await orders.GetForEditAsync(id, cancellationToken) ?? throw new PendingSaleNotSavableException($"order {id} no longer exists.");
        var advances = order.CustomerId is null ? [] : await orders.CountedAdvancesForUpdateAsync(order.Id, cancellationToken);
        var delivered = await orders.DeliveredByLineAsync([order.Id], null, cancellationToken);
        var shared = (await orders.InvoicesOfAsync(order.Id, cancellationToken)).Where(i => i.StatusId != StatusIds.SalesInvoice.Cancelled).Sum(i => i.DiscountAmount);
        return new SalesInvoiceOrderFacts(order, advances, delivered, shared);
    }

    /// <summary>The profile of the sale, or the one it was merged into while the transfer was awaited (SALE-04.3 moves invoices, not pending sales).</summary>
    private async Task<Customer?> CustomerAsync(Guid? customerId, CancellationToken cancellationToken)
    {
        if (customerId is not { } id)
        {
            return null;
        }

        var customer = await customers.GetForEditAsync(id, cancellationToken) ?? throw new PendingSaleNotSavableException($"customer profile {id} no longer exists.");
        for (var step = 0; customer.MergedIntoCustomerId is { } kept && step < MaxMergeSteps; step++)
        {
            customer = await customers.GetForEditAsync(kept, cancellationToken) ?? customer;
        }

        return customer;
    }
}
