using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The fields of an edit once their shapes are right. A null discount, other charges or note keeps the stored one.</summary>
internal sealed record SalesInvoiceEditEntries(
    string? Code, IReadOnlyList<SalesInvoiceLineEntries> Lines, IReadOnlySet<Guid> NotSoldWholeLotIds, decimal? Discount, decimal? OtherCharges, string? Note, bool NoteSent, bool ConfirmOwedBack);

/// <summary>One line of the edited invoice. <paramref name="Lot"/> is loaded with its product lines, so the product ledger can post the line.</summary>
internal sealed record EditedLine(
    SalesInvoiceLineEntries Entry, ResolvedLine Resolved, decimal UnitPrice, decimal ListPrice, SalesPriceSource PriceSource, decimal Amount, bool SoldWholeLot, HarvestLot Lot);

/// <summary>What the edit will write: the money of the invoice, its lines, the lots whose mark changed, the debt after, what is cut from the advance and the order the invoice delivers.</summary>
internal sealed record SalesInvoiceEditPlan(
    string Code, decimal Subtotal, decimal Discount, decimal OtherCharges, decimal Total, string? Note, IReadOnlyList<EditedLine> Lines, IReadOnlyList<HarvestLot> LotsToClose,
    IReadOnlyList<HarvestLot> LotsToReopen, IReadOnlyList<CustomerDebtAfterChange> DebtsAfter, AdvanceTrim Trim, SalesOrder? Order);

/// <summary>
/// The edit of an invoice against the database and the rules of the money, like the edit of a purchase receipt: the code (free, and fixed once a VietQR payment names the invoice), the lines as in
/// SALE-05 (the customer and the zone stay), the total (never below 0, never raised for a walk-in invoice, never under what a walk-in paid), and the debt it leaves. Nothing is written here.
/// </summary>
public sealed class SalesInvoiceEditPlanner(
    SalesInvoiceFactsReader factsReader,
    SalesInvoiceCodeService codes,
    SalesInvoiceAdvanceTrimmer trimmer,
    ISalesOrderRepository orders,
    IHarvestLotRepository lots,
    HarvestLotStatusService lotStatuses,
    CustomerDebtReader debts)
{
    internal async Task<Result<SalesInvoiceEditPlan>> PlanAsync(SalesInvoice invoice, SalesInvoiceEditEntries entries, CancellationToken cancellationToken)
    {
        var (code, duplicate) = await codes.ForEditAsync(entries.Code, invoice.Id, invoice.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        if (!string.Equals(code, invoice.Code, StringComparison.Ordinal)
            && invoice.Payments.Any(p => p.Method == CustomerPaymentMethod.VietQrInvoice && p.StatusId != Domain.Constants.StatusIds.CustomerPayment.Cancelled))
        {
            // The code is the content of the transfer that paid the invoice.
            return AppError.Invalid("Code", "The code cannot change once a VietQR transfer has paid the invoice.");
        }

        var order = invoice.SalesOrderId is { } orderId ? await orders.GetForEditAsync(orderId, cancellationToken) : null;
        var lines = await PlanLinesAsync(invoice, order, entries, cancellationToken);
        if (!lines.IsSuccess)
        {
            return lines.Error!;
        }

        var subtotal = InvoiceMoney.Subtotal(lines.Value!.Lines.Select(l => l.Amount));
        var discount = entries.Discount ?? invoice.DiscountAmount;
        var otherCharges = entries.OtherCharges ?? invoice.OtherCharges;
        var total = InvoiceMoney.Total(subtotal, discount, otherCharges, invoice.PriceCutTotal);
        if (total < 0m)
        {
            return AppError.Invalid("Discount");
        }

        // Amounts that fit their fields one by one can still overflow the columns of the invoice together.
        if (!InvoiceMoney.TotalsFit(subtotal, total, invoice.PriceCutTotal))
        {
            return AppError.Invalid("Total");
        }

        var paid = SalesInvoiceChangeMoney.PaidOf(invoice.Payments);
        if (invoice.Customer is null)
        {
            // BR-SALE-10 (2): a walk-in invoice was paid on the spot and has no profile to owe money back to: it is not raised, and not lowered under what was paid.
            if (total > invoice.Total)
            {
                return AppError.Conflict(UserMessages.WalkInTotalCannotBeRaised(invoice.Code));
            }

            if (total < paid)
            {
                return AppError.Invalid("Total", "A walk-in invoice cannot be lowered under what was paid: cancel it instead.");
            }
        }

        var trim = await trimmer.PlanAsync(invoice, total, cancellationToken);
        var debtsAfter = await DebtsAfterAsync(invoice, total, trim, cancellationToken);
        if (!entries.ConfirmOwedBack && debtsAfter.FirstOrDefault(d => d.DebtAfter < 0m) is { } owedBack)
        {
            var amount = CustomerDebtMath.OwedBack(owedBack.DebtAfter);
            return AppError.Conflict(
                UserMessages.MoneyWillBeOwedBack("edit", owedBack.Customer.Name, amount),
                new Dictionary<string, object?> { ["confirmationField"] = "confirmOwedBack", ["customerName"] = owedBack.Customer.Name, ["owedBackAmount"] = amount });
        }

        var note = entries.NoteSent ? entries.Note : invoice.Note;
        return new SalesInvoiceEditPlan(
            code, subtotal, discount, otherCharges, total, note, lines.Value.Lines, lines.Value.ToClose, lines.Value.ToReopen, debtsAfter, trim, order);
    }

    // What the customer owes apart from the advances held for open orders, once the invoice has its new total: the part cut from the advance of an open order stays held, so it is not free credit.
    private async Task<List<CustomerDebtAfterChange>> DebtsAfterAsync(SalesInvoice invoice, decimal total, AdvanceTrim trim, CancellationToken cancellationToken)
    {
        if (invoice.Customer is not { } customer)
        {
            return [];
        }

        var before = (await debts.AllocationAsync(customer.Id, cancellationToken)).DebtApartFromReserved;
        return [new CustomerDebtAfterChange(customer, SalesInvoiceChangeMoney.AfterCorrection(before, invoice.Total, total) + trim.ReservedGain)];
    }

    private async Task<Result<LinePlan>> PlanLinesAsync(SalesInvoice invoice, SalesOrder? order, SalesInvoiceEditEntries entries, CancellationToken cancellationToken)
    {
        var onInvoice = invoice.Lines.Select(l => l.ProductId).ToHashSet();
        var products = await factsReader.ReadProductsAsync(entries.Lines, onInvoice, cancellationToken);
        if (!products.IsSuccess)
        {
            return products.Error!;
        }

        var resolved = SalesInvoiceLineResolver.Resolve(entries.Lines, products.Value!, order, invoice.Lines.ToList());
        if (!resolved.IsSuccess)
        {
            return resolved.Error!;
        }

        var newLotIds = entries.Lines.Select(l => l.LotId).Distinct().ToList();
        var allLotIds = newLotIds.Union(invoice.Lines.Select(l => l.HarvestLotId)).ToList();
        var byId = (await lots.ListForInvoiceAsync(allLotIds, cancellationToken)).ToDictionary(l => l.Id);
        if (entries.Lines.FirstOrDefault(l => !byId.ContainsKey(l.LotId)) is { } unknown)
        {
            return AppError.Invalid($"Line {unknown.LineNumber} harvest lot");
        }

        // Only a lot that was not on the invoice must still be for sale (BR-SALE-10).
        var added = newLotIds.Where(id => invoice.Lines.All(l => l.HarvestLotId != id)).Select(id => byId[id]).ToList();
        if (lotStatuses.EnsureForSale(added) is { } closed)
        {
            return closed;
        }

        var lines = new List<EditedLine>();
        foreach (var e in entries.Lines)
        {
            var line = resolved.Value![e.LineNumber];
            var same = invoice.Lines.FirstOrDefault(l => l.ProductId == e.ProductId && l.UnitId == line.Unit.UnitId);
            // A product already on the invoice keeps the list price it was sold at (BR-SALE-05); a new one takes today's.
            var listPrice = same?.ListPrice ?? line.Unit.SalePrice;
            var unitPrice = e.UnitPrice ?? line.OrderLine?.UnitPrice ?? same?.UnitPrice ?? listPrice;
            var source = line.OrderLine is not null && unitPrice == line.OrderLine.UnitPrice ? SalesPriceSource.OrderPrice
                : unitPrice == listPrice ? SalesPriceSource.PriceList
                : SalesPriceSource.DecidedByOwner;
            if (!InvoiceMoney.TryLineAmount(line.Quantity, unitPrice, out var amount))
            {
                return AppError.Invalid($"Line {e.LineNumber} amount");
            }

            lines.Add(new EditedLine(e, line, unitPrice, listPrice, source, amount, !entries.NotSoldWholeLotIds.Contains(e.LotId), byId[e.LotId]));
        }

        // BR-HARVEST-04: only a mark that changed acts on a lot; an unchanged tick leaves it alone whatever its status has become since.
        var wasTicked = invoice.Lines.GroupBy(l => l.HarvestLotId).ToDictionary(g => g.Key, g => g.Any(l => l.SoldWholeLot));
        var ticked = entries.Lines.Select(l => l.LotId).Where(id => !entries.NotSoldWholeLotIds.Contains(id)).ToHashSet();
        var toClose = byId.Values.Where(l => ticked.Contains(l.Id) && !wasTicked.GetValueOrDefault(l.Id) && l.IsForSale).ToList();
        var toReopen = byId.Values.Where(l => !ticked.Contains(l.Id) && wasTicked.GetValueOrDefault(l.Id)).ToList();
        return new LinePlan(lines, toClose, toReopen);
    }

    private sealed record LinePlan(IReadOnlyList<EditedLine> Lines, IReadOnlyList<HarvestLot> ToClose, IReadOnlyList<HarvestLot> ToReopen);
}
