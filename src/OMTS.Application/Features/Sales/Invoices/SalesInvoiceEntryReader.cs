namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>One line as read: the product, the lot, the kg and the price entered (null: the list price).</summary>
internal sealed record SalesInvoiceLineEntries(int LineNumber, Guid ProductId, Guid LotId, decimal Kg, decimal? UnitPrice, bool PriceDecidedByOwner);

/// <summary>The fields of SALE-05 once their shapes are right; what they name is checked against the database after.</summary>
internal sealed record SalesInvoiceEntries(
    Guid? CustomerId,
    Guid? ZoneId,
    Guid? RequestId,
    bool TakesRequestInFull,
    IReadOnlyList<SalesInvoiceLineEntries> Lines,
    IReadOnlySet<Guid> NotSoldWholeLotIds,
    decimal Discount,
    bool DiscountDecidedByOwner,
    decimal OtherCharges,
    decimal Cash,
    SalesInvoiceRestPayment Rest,
    bool? SubtractExtraFromDebt,
    bool PayLaterOwnerAgreed,
    Guid? PortWeighingId = null)
{
    public bool IsWalkIn => CustomerId is null;

    /// <summary>The rest after the cash is paid with the VietQR code of the invoice (SALE-05 steps 7-8).</summary>
    public bool RestByVietQr => Rest == SalesInvoiceRestPayment.VietQr;

    /// <summary>The lots of the invoice, in the order first added: the first one proposes the zone (BR-SALE-08).</summary>
    public IReadOnlyList<Guid> LotIds => Lines.Select(l => l.LotId).Distinct().ToList();
}

/// <summary>SALE-05.0.E1: the shape of each entry (MSG-GEN-01 names every field to fix). A line of 0 kg is never saved (AC-06).</summary>
internal static class SalesInvoiceEntryReader
{
    public const int MaxLines = 100;

    public static (SalesInvoiceEntries? Entries, List<string> Invalid) Read(CreateSalesInvoiceRequest request)
    {
        var invalid = new List<string>();
        var walkIn = request.CustomerId is null;

        // A walk-in customer is chosen, never assumed (SALE-05.2): a missing profile is a mistake, and a profile with the walk-in mark is two answers.
        if (walkIn != request.WalkInCustomer)
        {
            invalid.Add("Customer");
        }

        var lines = ReadLines(request.Lines, invalid);
        var lotIds = lines.Select(l => l.LotId).ToHashSet();
        var notSoldWhole = (request.NotSoldWholeLotIds ?? []).ToHashSet();
        if (!notSoldWhole.IsSubsetOf(lotIds))
        {
            invalid.Add("Sold the whole lot");
        }

        // A request belongs to a profile (BR-SALE-01); the mark has no meaning without one.
        if ((request.PurchaseRequestId is not null && walkIn) || (request.PurchaseRequestId is null && request.TakesRequestInFull is not null))
        {
            invalid.Add("Purchase request");
        }

        // A request is in whole cages, so its invoice comes from a weighing both sides confirmed (decision of 09/11/2026); a sale without a request gives the kg itself.
        if ((request.PurchaseRequestId is null) != (request.PortWeighingId is null))
        {
            invalid.Add("Port weighing");
        }

        var discount = request.Discount ?? 0m;
        if (!InvoiceMoney.IsWholeAmount(discount))
        {
            invalid.Add("Discount");
        }

        var otherCharges = request.OtherCharges ?? 0m;
        if (!InvoiceMoney.IsWholeAmount(otherCharges))
        {
            invalid.Add("Other charges");
        }

        var payment = request.Payment;
        if (payment is null || !InvoiceMoney.IsWholeAmount(payment.CashAmount) || !Enum.IsDefined(payment.Rest))
        {
            invalid.Add("Payment");
        }
        else if (walkIn && payment.Rest == SalesInvoiceRestPayment.PayLater)
        {
            // SALE-05.2: Pay later is not offered to a walk-in customer.
            invalid.Add("Pay later");
        }

        if (invalid.Count > 0)
        {
            return (null, invalid);
        }

        return (new SalesInvoiceEntries(
            request.CustomerId,
            request.ZoneId,
            request.PurchaseRequestId,
            request.PurchaseRequestId is not null && (request.TakesRequestInFull ?? true),
            lines,
            notSoldWhole,
            discount,
            request.DiscountDecidedByOwner,
            otherCharges,
            payment!.CashAmount!.Value,
            payment.Rest,
            payment.SubtractExtraFromDebt,
            payment.PayLaterOwnerAgreed,
            request.PortWeighingId), invalid);
    }

    internal static List<SalesInvoiceLineEntries> ReadLines(IReadOnlyList<SalesInvoiceLineEntry?>? entries, List<string> invalid)
    {
        var lines = new List<SalesInvoiceLineEntries>();
        if (entries is null || entries.Count == 0 || entries.Count > MaxLines)
        {
            invalid.Add("Lines");
            return lines;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var number = i + 1;
            var line = entries[i];
            var before = invalid.Count;
            if (line?.ProductId is null)
            {
                invalid.Add($"Line {number} product");
            }

            if (line?.HarvestLotId is null)
            {
                invalid.Add($"Line {number} harvest lot");
            }

            if (!InvoiceMoney.IsKg(line?.Kg))
            {
                invalid.Add($"Line {number} kg");
            }

            if (line?.UnitPrice is { } price && (price <= 0m || !InvoiceMoney.IsWholeAmount(price)))
            {
                invalid.Add($"Line {number} unit price");
            }

            if (invalid.Count == before)
            {
                lines.Add(new SalesInvoiceLineEntries(number, line!.ProductId!.Value, line.HarvestLotId!.Value, line.Kg!.Value, line.UnitPrice, line.PriceDecidedByOwner));
            }
        }

        return lines;
    }
}
