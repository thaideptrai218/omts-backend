using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>One line of the corrected invoice.</summary>
internal sealed record CorrectedLine(SalesInvoiceLineEntries Entry, decimal UnitPrice, decimal ListPrice, SalesPriceSource PriceSource, decimal Amount, bool SoldWholeLot);

/// <summary>The debt of a profile once the save is done (BR-DEBT-02).</summary>
internal sealed record CustomerDebtAfterChange(Customer Customer, decimal DebtAfter);

/// <summary>What SALE-07 step 8 will write: the money of the invoice, the lines of a data entry correction, the profile and the lots whose mark changed.</summary>
internal sealed record SalesInvoiceCorrectionPlan(
    decimal Subtotal, decimal Discount, decimal OtherCharges, decimal PriceCutTotal, decimal Total, IReadOnlyList<CorrectedLine> Lines, Customer? Customer, bool CustomerChanged,
    IReadOnlyList<HarvestLot> LotsToClose, IReadOnlyList<HarvestLot> LotsToReopen, IReadOnlyList<CustomerDebtAfterChange> DebtsAfter);

/// <summary>
/// SALE-07 steps 5 and 3 against the database and the rules of the money: the profile, the lines as in SALE-05, the total (never below 0, never raised for a walk-in invoice) and the
/// debt it leaves. Questions and refusals are answered in the order of the use case; nothing is written here.
/// </summary>
public sealed class SalesInvoiceCorrectionPlanner(SalesInvoiceFactsReader factsReader, IHarvestLotRepository lots, HarvestLotStatusService lotStatuses, CustomerDebtReader debts)
{
    internal async Task<Result<SalesInvoiceCorrectionPlan>> PlanAsync(SalesInvoice invoice, SalesInvoiceCorrectionEntries entries, bool confirmOwedBack, CancellationToken cancellationToken)
    {
        // A price cut gives a profile only to a walk-in invoice with money to give back (SALE-07.2); another profile is a data entry correction (SALE-07.5).
        if (entries.IsPriceCut && entries.CustomerId is not null && invoice.CustomerId is not null)
        {
            return AppError.Invalid("Customer");
        }

        var chosen = await factsReader.ReadCustomerAsync(entries.CustomerId, cancellationToken);
        if (!chosen.IsSuccess)
        {
            return chosen.Error!;
        }

        var customer = chosen.Value ?? invoice.Customer;
        var customerChanged = customer?.Id != invoice.CustomerId;

        var lines = entries.IsPriceCut ? LinePlan.None : await PlanLinesAsync(invoice, entries, cancellationToken);
        if (!lines.IsSuccess)
        {
            return lines.Error!;
        }

        var subtotal = entries.IsPriceCut ? invoice.Subtotal : InvoiceMoney.Subtotal(lines.Value!.Lines.Select(l => l.Amount));
        var discount = entries.Discount ?? invoice.DiscountAmount;
        var otherCharges = entries.OtherCharges ?? invoice.OtherCharges;
        var priceCutTotal = invoice.PriceCutTotal + entries.PriceCutAmount;
        var total = InvoiceMoney.Total(subtotal, discount, otherCharges, priceCutTotal);
        if (total < 0m)
        {
            return AppError.Invalid(entries.IsPriceCut ? "Price cut amount" : "Discount");
        }

        var paid = SalesInvoiceChangeMoney.PaidOf(invoice.Payments);
        if (customer is null)
        {
            if (total > invoice.Total)
            {
                return AppError.Conflict(UserMessages.WalkInTotalCannotBeRaised(invoice.Code));
            }

            if (paid > total)
            {
                return AppError.Conflict(
                    UserMessages.WalkInBuyerProfileNeeded(invoice.Code, paid - total),
                    new Dictionary<string, object?> { ["confirmationField"] = "customerId", ["owedBackAmount"] = paid - total });
            }
        }

        var debtsAfter = await DebtsAfterAsync(invoice, customer, customerChanged, total, paid, cancellationToken);
        if (!confirmOwedBack && debtsAfter.FirstOrDefault(d => d.DebtAfter < 0m) is { } owedBack)
        {
            var amount = CustomerDebtMath.OwedBack(owedBack.DebtAfter);
            return AppError.Conflict(
                UserMessages.MoneyWillBeOwedBack("correction", owedBack.Customer.Name, amount),
                new Dictionary<string, object?> { ["confirmationField"] = "confirmOwedBack", ["customerName"] = owedBack.Customer.Name, ["owedBackAmount"] = amount });
        }

        return new SalesInvoiceCorrectionPlan(
            subtotal, discount, otherCharges, priceCutTotal, total, lines.Value!.Lines, customer, customerChanged, lines.Value.ToClose, lines.Value.ToReopen, debtsAfter);
    }

    // The debt of every profile the correction touches; none for an invoice that stays walk-in.
    private async Task<List<CustomerDebtAfterChange>> DebtsAfterAsync(SalesInvoice invoice, Customer? customer, bool customerChanged, decimal total, decimal paid, CancellationToken cancellationToken)
    {
        var profiles = new[] { invoice.Customer, customer }.OfType<Customer>().DistinctBy(c => c.Id).ToList();
        var before = await debts.DebtsAsync(profiles.Select(c => c.Id).ToList(), cancellationToken);
        return profiles.Select(p =>
        {
            var debt = before[p.Id];
            var after = !customerChanged ? SalesInvoiceChangeMoney.AfterCorrection(debt, invoice.Total, total)
                : p.Id == invoice.CustomerId ? SalesInvoiceChangeMoney.AfterLeaving(debt, invoice.Total, paid)
                : SalesInvoiceChangeMoney.AfterComingIn(debt, total, paid);
            return new CustomerDebtAfterChange(p, after);
        }).ToList();
    }

    private async Task<Result<LinePlan>> PlanLinesAsync(SalesInvoice invoice, SalesInvoiceCorrectionEntries entries, CancellationToken cancellationToken)
    {
        var onInvoice = invoice.Lines.Select(l => l.ProductId).ToHashSet();
        var products = await factsReader.ReadProductsAsync(entries.Lines, onInvoice, cancellationToken);
        if (!products.IsSuccess)
        {
            return products.Error!;
        }

        var newLotIds = entries.Lines.Select(l => l.LotId).Distinct().ToList();
        var allLotIds = newLotIds.Union(invoice.Lines.Select(l => l.HarvestLotId)).ToList();
        var byId = (await lots.ListForInvoiceAsync(allLotIds, cancellationToken)).ToDictionary(l => l.Id);
        if (entries.Lines.FirstOrDefault(l => !byId.ContainsKey(l.LotId)) is { } unknown)
        {
            return AppError.Invalid($"Line {unknown.LineNumber} harvest lot");
        }

        // SALE-07.0.E3: only a lot that was not on the invoice must still be for sale (BR-SALE-10).
        var added = newLotIds.Where(id => invoice.Lines.All(l => l.HarvestLotId != id)).Select(id => byId[id]).ToList();
        if (lotStatuses.EnsureForSale(added) is { } closed)
        {
            return closed;
        }

        var lines = entries.Lines.Select(e =>
        {
            var product = products.Value![e.ProductId];
            // A product already on the invoice keeps the list price it was sold at (BR-SALE-05); a new one takes today's.
            var listPrice = invoice.Lines.FirstOrDefault(l => l.ProductId == e.ProductId)?.ListPrice ?? product.UnitPrice;
            var unitPrice = e.UnitPrice ?? listPrice;
            var source = unitPrice == listPrice ? SalesPriceSource.PriceList : SalesPriceSource.DecidedByOwner;
            return new CorrectedLine(e, unitPrice, listPrice, source, InvoiceMoney.LineAmount(e.Kg, unitPrice), !entries.NotSoldWholeLotIds.Contains(e.LotId));
        }).ToList();

        // BR-HARVEST-04: only a mark that changed acts on a lot; an unchanged tick leaves it alone whatever its status has become since.
        var wasTicked = invoice.Lines.GroupBy(l => l.HarvestLotId).ToDictionary(g => g.Key, g => g.Any(l => l.SoldWholeLot));
        var ticked = entries.Lines.Select(l => l.LotId).Where(id => !entries.NotSoldWholeLotIds.Contains(id)).ToHashSet();
        var toClose = byId.Values.Where(l => ticked.Contains(l.Id) && !wasTicked.GetValueOrDefault(l.Id) && l.IsForSale).ToList();
        var toReopen = byId.Values.Where(l => !ticked.Contains(l.Id) && wasTicked.GetValueOrDefault(l.Id)).ToList();
        return new LinePlan(lines, toClose, toReopen);
    }

    private sealed record LinePlan(IReadOnlyList<CorrectedLine> Lines, IReadOnlyList<HarvestLot> ToClose, IReadOnlyList<HarvestLot> ToReopen)
    {
        public static readonly Result<LinePlan> None = new LinePlan([], [], []);
    }
}
