using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>One line with its price and amount, as it will be saved.</summary>
/// <param name="ListPrice">The list price of the product when the sale was decided (a sale paid by VietQR is saved later, at the prices shown).</param>
internal sealed record PricedLine(SalesInvoiceLineEntries Entry, Product Product, HarvestLot Lot, decimal UnitPrice, SalesPriceSource PriceSource, decimal Amount, decimal ListPrice);

/// <summary>The money of the invoice and how it is paid at the port.</summary>
/// <param name="LaterAmount">What the invoice adds to the customer debt: the rest after the cash part with Pay later, else 0.</param>
/// <param name="VietQrAmount">The amount of the VietQR code: the rest after the cash part when it is paid by VietQR, else 0 (the sale then waits for the transfer).</param>
internal sealed record SalesInvoiceDecision(
    IReadOnlyList<PricedLine> Lines, decimal Subtotal, decimal Total, PortCashSplit Cash, SalesPaymentTerm Term, decimal LaterAmount, decimal VietQrAmount = 0m)
{
    public bool AwaitsTransfer => VietQrAmount > 0m;
}

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
            lines.Add(new PricedLine(entry, product, lots[entry.LotId], unitPrice, source, InvoiceMoney.LineAmount(entry.Kg, unitPrice), product.UnitPrice));
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
        if (rest > 0m && entries.RestByVietQr)
        {
            // BR-SALE-07 (1), (5): the VietQR code of the invoice is for the rest; the invoice is saved Pay now when the transfer arrives (SALE-05 steps 7-8).
            var split = InvoiceMoney.SplitCash(total, entries.Cash, 0m, subtractExtraFromDebt: false);
            return new SalesInvoiceDecision(lines, subtotal, total, split, term, 0m, split.Rest);
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
        return new SalesInvoiceDecision(lines, subtotal, total, cash, term, term == SalesPaymentTerm.PayLater ? cash.Rest : 0m);
    }

    private static AppError Question(UserMessage message, string confirmationField, Dictionary<string, object?> details)
    {
        details["confirmationField"] = confirmationField;
        return AppError.Conflict(message, details);
    }
}
