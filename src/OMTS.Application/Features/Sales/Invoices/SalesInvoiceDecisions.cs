using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>One line with its price and amount, as it will be saved.</summary>
internal sealed record PricedLine(SalesInvoiceLineEntries Entry, Product Product, HarvestLot Lot, decimal UnitPrice, SalesPriceSource PriceSource, decimal Amount);

/// <summary>The money of the invoice and how it is paid at the port.</summary>
/// <param name="LaterAmount">What the invoice adds to the customer debt: the rest after the cash part with Pay later, else 0.</param>
internal sealed record SalesInvoiceDecision(IReadOnlyList<PricedLine> Lines, decimal Subtotal, decimal Total, PortCashSplit Cash, SalesPaymentTerm Term, decimal LaterAmount);

/// <summary>
/// SALE-05 steps 4 to 6 as pure rules: prices from the price list unless the Cooperative Owner decided otherwise (BR-SALE-06, MSG-SALE-10), the total (never below 0), and the payment
/// at the port (BR-SALE-07): Pay later only with the Owner's agreement (MSG-SALE-12), cash above the total subtracted from the debt only up to it (MSG-SALE-11). One question at a time is
/// answered with a 409 whose <c>confirmationField</c> names the flag to send Yes or No in the same request again.
/// </summary>
internal static class SalesInvoiceDecisions
{
    public static Result<SalesInvoiceDecision> Decide(SalesInvoiceEntries entries, SalesInvoiceFacts facts)
    {
        var lots = facts.Lots.ToDictionary(l => l.Id);
        var lines = new List<PricedLine>();
        foreach (var entry in entries.Lines)
        {
            var product = facts.Products[entry.ProductId];
            var unitPrice = entry.UnitPrice ?? product.UnitPrice;
            if (unitPrice != product.UnitPrice && !facts.ByOwner && !entry.PriceDecidedByOwner)
            {
                return Question(
                    UserMessages.PriceNeedsOwnerDecision(product.Name, product.UnitPrice, unitPrice),
                    $"lines[{entry.LineNumber - 1}].priceDecidedByOwner",
                    new() { ["lineNumber"] = entry.LineNumber });
            }

            var source = unitPrice == product.UnitPrice ? SalesPriceSource.PriceList : SalesPriceSource.DecidedByOwner;
            lines.Add(new PricedLine(entry, product, lots[entry.LotId], unitPrice, source, InvoiceMoney.LineAmount(entry.Kg, unitPrice)));
        }

        var subtotal = InvoiceMoney.Subtotal(lines.Select(l => l.Amount));
        var total = InvoiceMoney.Total(subtotal, entries.Discount, entries.OtherCharges);
        if (total < 0m)
        {
            // BR-SALE-06: the discount cannot bring the total below 0.
            return AppError.Invalid("Discount");
        }

        if (entries.Discount > 0m && !facts.ByOwner && !entries.DiscountDecidedByOwner)
        {
            return Question(UserMessages.DiscountNeedsOwnerDecision(entries.Discount), "discountDecidedByOwner", new());
        }

        return DecidePayment(entries, facts, lines, subtotal, total);
    }

    private static Result<SalesInvoiceDecision> DecidePayment(SalesInvoiceEntries entries, SalesInvoiceFacts facts, List<PricedLine> lines, decimal subtotal, decimal total)
    {
        var customerName = facts.Customer?.Name ?? string.Empty;
        var rest = total - Math.Min(entries.Cash, total);
        var term = SalesPaymentTerm.PayNow;
        if (rest > 0m)
        {
            if (entries.Rest != SalesInvoiceRestPayment.PayLater)
            {
                // BR-SALE-07 (5): otherwise the rest is paid now. VietQR for the rest comes with the pending sale (PR 7).
                return AppError.Invalid("Cash amount", "The cash is less than the total: choose Pay later for the rest, or take the whole total.");
            }

            if (facts.Request?.PayLaterAgreed != true && !entries.PayLaterOwnerAgreed)
            {
                return Question(
                    UserMessages.PayLaterNeedsOwnerAgreement(customerName, rest, facts.DebtBefore),
                    "payment.payLaterOwnerAgreed",
                    new() { ["laterAmount"] = rest, ["debtAmount"] = facts.DebtBefore });
            }

            term = SalesPaymentTerm.PayLater;
        }

        if (InvoiceMoney.ExtraCanLowerDebt(total, entries.Cash, facts.DebtBefore) && facts.Customer is not null && entries.SubtractExtraFromDebt is null)
        {
            var asked = InvoiceMoney.SplitCash(total, entries.Cash, facts.DebtBefore, subtractExtraFromDebt: true);
            return Question(
                UserMessages.CashAboveTotal(asked.Extra, customerName, facts.DebtBefore, asked.SubtractedFromDebt, asked.Change),
                "payment.subtractExtraFromDebt",
                new() { ["excessAmount"] = asked.Extra, ["debtAmount"] = facts.DebtBefore, ["subtractAmount"] = asked.SubtractedFromDebt, ["changeAmount"] = asked.Change });
        }

        var subtract = facts.Customer is not null && entries.SubtractExtraFromDebt == true;
        var cash = InvoiceMoney.SplitCash(total, entries.Cash, facts.Customer is null ? 0m : facts.DebtBefore, subtract);
        return new SalesInvoiceDecision(lines, subtotal, total, cash, term, term == SalesPaymentTerm.PayLater ? cash.Rest : 0m);
    }

    private static AppError Question(UserMessage message, string confirmationField, Dictionary<string, object?> details)
    {
        details["confirmationField"] = confirmationField;
        return AppError.Conflict(message, details);
    }
}
