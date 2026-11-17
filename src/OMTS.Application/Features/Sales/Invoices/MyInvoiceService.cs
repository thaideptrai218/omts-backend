using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices.Dtos;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The trader's own invoices, with the kg of each cage (§I.4.4 †: only the trader's own data). The API checks that the caller is a trader; an invoice of another profile answers as
/// not found, like a request of another trader (SALE-10). Read only.
/// </summary>
public sealed class MyInvoiceService(
    ISalesInvoiceRepository invoices,
    ICustomerRepository customers,
    CustomerDebtReader debts,
    SalesInvoiceViews views,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser)
{
    private const int MaxPageSize = 100;

    public async Task<Result<MyInvoiceList>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (await ProfileAsync(cancellationToken) is not { } customer)
        {
            return AppError.Invalid("Customer profile");
        }

        // The page number is capped so the offset cannot overflow.
        var (pageNumber, size) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var (rows, total) = await invoices.ListOfCustomerAsync(customer.Id, pageNumber, size, cancellationToken);
        var allocation = await debts.AllocationAsync(customer.Id, cancellationToken);
        var items = rows.Select(row =>
        {
            var state = SalesInvoicePaidState.Of(row.Id, row.StatusId, row.Total, customer.Id, ownPayments: 0m, allocation);
            return new MyInvoiceRow(
                row.Id, row.Code, row.InvoicedAt, row.InvoiceDate, row.ZoneName, row.PurchaseRequestCode, row.CageCount, row.TotalKg, row.Total, row.StatusId,
                statusCatalog.DisplayNameOf(row.StatusId), row.PaymentTerm, state.IsPaid, state.Unpaid);
        }).ToList();
        var empty = total == 0 ? UserMessages.NothingToShowYet : null;
        return new MyInvoiceList(new Paged<MyInvoiceRow>(items, pageNumber, size, total), empty?.Text, empty?.Code);
    }

    public async Task<Result<MyInvoiceView>> GetAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        if (await ProfileAsync(cancellationToken) is not { } customer)
        {
            return AppError.Invalid("Customer profile");
        }

        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null || invoice.CustomerId != customer.Id)
        {
            return AppError.NotFound;
        }

        var allocation = await debts.AllocationAsync(customer.Id, cancellationToken);
        var state = SalesInvoicePaidState.Of(invoice.Id, invoice.StatusId, invoice.Total, customer.Id, ownPayments: 0m, allocation);
        var lines = invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new MyInvoiceLineView(
            l.LineNumber, l.Product.Name, l.HarvestLot.LotCode, l.Kg, l.UnitPrice, l.Amount, SalesInvoiceViews.CageKgsOf(invoice, l.HarvestLotId))).ToList();
        var payments = invoice.Payments.Where(p => p.Kind == CustomerPaymentKind.Payment).OrderBy(p => p.CreatedAt).ThenBy(p => p.Code).Select(p => new MyInvoicePaymentView(
            p.Code, p.Kind, p.Method, p.Amount, p.PaymentDate, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId))).ToList();
        return new MyInvoiceView(
            invoice.Id, invoice.Code, invoice.InvoicedAt, invoice.InvoiceDate, invoice.Zone.Name, invoice.PurchaseRequestId, invoice.PurchaseRequest?.Code, lines,
            lines.Sum(l => l.CageKgs.Count), lines.Sum(l => l.Kg), invoice.Subtotal, invoice.DiscountAmount, invoice.OtherCharges, invoice.PriceCutTotal, invoice.Total, invoice.PaymentTerm,
            invoice.StatusId, statusCatalog.DisplayNameOf(invoice.StatusId), state.IsPaid, state.Covered, state.Unpaid, payments, views.QrUrlOf(invoice), invoice.PortWeighingId);
    }

    /// <summary>The profile linked to the trader's account; a merged profile holds nothing any more.</summary>
    private async Task<Customer?> ProfileAsync(CancellationToken cancellationToken)
    {
        var customer = await customers.GetByAccountIdAsync(currentUser.AccountId!.Value, cancellationToken);
        return customer is null || customer.MergedIntoCustomerId is not null ? null : customer;
    }
}
