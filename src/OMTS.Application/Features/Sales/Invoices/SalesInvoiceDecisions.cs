using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>One line with its unit, price and amount, as it will be saved.</summary>
/// <param name="Resolved">The unit, the quantity in it and the order line the line delivers.</param>
/// <param name="UnitPrice">Per unit of the line (per kg for a line of kg).</param>
/// <param name="ListPrice">The price of the unit on the price list when the sale was decided (a sale paid by VietQR is saved later, at the prices shown).</param>
internal sealed record PricedLine(
    SalesInvoiceLineEntries Entry, Product Product, HarvestLot Lot, ResolvedLine Resolved, decimal UnitPrice, SalesPriceSource PriceSource, decimal Amount, decimal ListPrice);

/// <summary>The money of the invoice and how it is paid at the port.</summary>
/// <param name="LaterAmount">What the invoice adds to the customer debt: the rest after the advance and the cash part with Pay later, else 0.</param>
/// <param name="VietQrAmount">The amount of the VietQR code: the rest after the advance and the cash part when it is paid by VietQR, else 0 (the sale then waits for the transfer).</param>
/// <param name="AdvanceUse">The part of the total covered by the reserved advance of the order, set against the invoice when it is saved.</param>
internal sealed record SalesInvoiceDecision(
    IReadOnlyList<PricedLine> Lines, decimal Subtotal, decimal Total, PortCashSplit Cash, SalesPaymentTerm Term, decimal LaterAmount, decimal VietQrAmount = 0m, decimal AdvanceUse = 0m)
{
    public bool AwaitsTransfer => VietQrAmount > 0m;
}

/// <summary>
/// SALE-05 steps 4 to 6 as pure rules: the price of the order line unless the Cooperative Owner decided otherwise (BR-SALE-06, MSG-SALE-10), the total (never below 0), and the payment
/// at the port (BR-SALE-07): the advance of the order pays first, then Pay later only with the Owner's agreement (MSG-SALE-12), cash above what is left subtracted from the debt only up to it
/// (MSG-SALE-11). One question at a time is answered with a 409 whose <c>confirmationField</c> names the flag to send Yes or No in the same request again.
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
            var resolved = facts.Resolved[entry.LineNumber];
            // The price asked about is the price of the order line; a line outside the order is compared with the price list.
            var reference = resolved.OrderLine?.UnitPrice ?? resolved.Unit.SalePrice;
            var unitPrice = entry.UnitPrice ?? reference;
            if (unitPrice != reference && !facts.ByOwner && !entry.PriceDecidedByOwner)
            {
                return Question(
                    UserMessages.OrderPriceNeedsOwnerDecision(product.Name, resolved.Unit.Name, reference, unitPrice),
                    $"lines[{entry.LineNumber - 1}].priceDecidedByOwner",
                    new() { ["lineNumber"] = entry.LineNumber });
            }

            var source = unitPrice != reference ? SalesPriceSource.DecidedByOwner : resolved.OrderLine is null ? SalesPriceSource.PriceList : SalesPriceSource.OrderPrice;
            if (!InvoiceMoney.TryLineAmount(resolved.Quantity, unitPrice, out var amount))
            {
                return AppError.Invalid($"Line {entry.LineNumber} amount");
            }

            lines.Add(new PricedLine(entry, product, lots[entry.LotId], resolved, unitPrice, source, amount, resolved.Unit.SalePrice));
        }

        var subtotal = InvoiceMoney.Subtotal(lines.Select(l => l.Amount));
        var total = InvoiceMoney.Total(subtotal, entries.Discount, entries.OtherCharges);
        if (total < 0m)
        {
            // BR-SALE-06: the discount cannot bring the total below 0.
            return AppError.Invalid("Discount");
        }

        if (!InvoiceMoney.TotalsFit(subtotal, total))
        {
            return AppError.Invalid("Total");
        }

        if (entries.Discount > 0m && !facts.ByOwner && !entries.DiscountDecidedByOwner && !WithinOrderDiscount(entries.Discount, subtotal, facts))
        {
            return Question(UserMessages.DiscountNeedsOwnerDecision(entries.Discount), "discountDecidedByOwner", new());
        }

        return DecidePayment(entries, facts, lines, subtotal, total);
    }

    /// <summary>The discount was decided with the order, so an invoice that takes no more than its share of it needs no new answer.</summary>
    private static bool WithinOrderDiscount(decimal discount, decimal subtotal, SalesInvoiceFacts facts) =>
        facts.Order is { } order
        && order.Order.DiscountAmount > 0m
        && discount <= SalesInvoiceOrderShare.DiscountShare(order.Order.DiscountAmount, order.Order.Subtotal, subtotal, order.DiscountShared);

    private static Result<SalesInvoiceDecision> DecidePayment(SalesInvoiceEntries entries, SalesInvoiceFacts facts, List<PricedLine> lines, decimal subtotal, decimal fullTotal)
    {
        var customerName = facts.Customer?.Name ?? string.Empty;
        if (facts.Customer is null && entries.Rest == SalesInvoiceRestPayment.PayLater)
        {
            // SALE-05.2: Pay later is not offered to a walk-in customer.
            return AppError.Invalid("Pay later");
        }

        // The advance the customer paid on the order pays first; the cash, Pay later and VietQR rules then apply to what is left.
        var advance = facts is { Customer: not null, Order: { } orderFacts } ? Math.Min(orderFacts.AdvanceLeft, fullTotal) : 0m;
        var total = fullTotal - advance;
        var rest = total - Math.Min(entries.Cash, total);
        var term = SalesPaymentTerm.PayNow;
        if (rest > 0m && entries.RestByVietQr)
        {
            // BR-SALE-07 (1), (5): the VietQR code of the invoice is for the rest; the invoice is saved Pay now when the transfer arrives (SALE-05 steps 7-8).
            var split = InvoiceMoney.SplitCash(total, entries.Cash, 0m, subtractExtraFromDebt: false);
            return new SalesInvoiceDecision(lines, subtotal, fullTotal, split, term, 0m, split.Rest, advance);
        }

        if (rest > 0m)
        {
            if (entries.Rest != SalesInvoiceRestPayment.PayLater)
            {
                // BR-SALE-07 (5): otherwise the rest is paid now.
                return AppError.Invalid("Cash amount", "The cash is less than the total: choose VietQR or Pay later for the rest, or take the whole total.");
            }

            // The debt warning of the settings: a customer over the limit gets no more debt, not even with the Owner's agreement.
            if (facts is { AllowMoreDebtOverLimit: false, Standing: { Over: true } standing })
            {
                return AppError.Conflict(UserMessages.CustomerOverDebtLimit(customerName, facts.DebtBefore, standing.DebtDays, standing.OverAmount, standing.OverDays));
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
        return new SalesInvoiceDecision(lines, subtotal, fullTotal, cash, term, term == SalesPaymentTerm.PayLater ? cash.Rest : 0m, 0m, advance);
    }

    private static AppError Question(UserMessage message, string confirmationField, Dictionary<string, object?> details)
    {
        details["confirmationField"] = confirmationField;
        return AppError.Conflict(message, details);
    }
}
