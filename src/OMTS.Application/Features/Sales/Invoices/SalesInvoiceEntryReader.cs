using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>One line as read: the product, the lot, the kg weighed, and what the line names besides (its order line, its unit and quantity, the price entered; null: the order price).</summary>
internal sealed record SalesInvoiceLineEntries(
    int LineNumber, Guid ProductId, Guid LotId, decimal Kg, decimal? UnitPrice, bool PriceDecidedByOwner, Guid? OrderLineId = null, Guid? UnitId = null, decimal? Quantity = null);

/// <summary>
/// The fields of SALE-05 once their shapes are right; what they name is checked against the database after. The customer and the zone are the ones the request sent (they are the order's, and only checked
/// against it); an invoice of the old flow, with no order, takes them from the request and the first lot.
/// </summary>
/// <param name="WalkInRequested">The request said walk-in customer.</param>
/// <param name="Code">The code the user typed; null takes the next one.</param>
internal sealed record SalesInvoiceEntries(
    Guid? CustomerId,
    bool WalkInRequested,
    Guid? ZoneId,
    Guid? SalesOrderId,
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
    string? Code = null,
    string? Note = null)
{
    /// <summary>The rest after the cash is paid with the VietQR code of the invoice (SALE-05 steps 7-8).</summary>
    public bool RestByVietQr => Rest == SalesInvoiceRestPayment.VietQr;

    /// <summary>The lots of the invoice, in the order first added: the first one proposes the zone of an invoice with no order (BR-SALE-08).</summary>
    public IReadOnlyList<Guid> LotIds => Lines.Select(l => l.LotId).Distinct().ToList();
}

/// <summary>SALE-05.0.E1: the shape of each entry (MSG-GEN-01 names every field to fix). A line of 0 kg is never saved (AC-06).</summary>
internal static class SalesInvoiceEntryReader
{
    public const int MaxLines = 100;
    public const int MaxNoteLength = 1000;

    /// <summary>The longest code the VietQR transfer content holds.</summary>
    public const int MaxVietQrCodeLength = 25;

    /// <param name="requireOrder">
    /// A new invoice names its order. A sale that was awaiting its VietQR transfer before invoices came from orders is read again without one (it is saved with no order), and a request link is
    /// refused for a new invoice.
    /// </param>
    public static (SalesInvoiceEntries? Entries, List<string> Invalid) Read(CreateSalesInvoiceRequest request, bool requireOrder = true)
    {
        var invalid = new List<string>();
        var walkIn = request.CustomerId is null;
        var hasOrder = request.SalesOrderId is not null;
        if (requireOrder && !hasOrder)
        {
            invalid.Add("Sales order");
        }

        // The customer of an invoice is its order's; only a sale that was awaiting its transfer before invoices came from orders names its own.
        var ordered = hasOrder || requireOrder;
        if (ordered)
        {
            // The customer is the order's: a profile with the walk-in mark is two answers, and a request link belongs to the old flow.
            if (!walkIn && request.WalkInCustomer)
            {
                invalid.Add("Customer");
            }

            if (request.PurchaseRequestId is not null || request.TakesRequestInFull is not null)
            {
                invalid.Add("Purchase request");
            }
        }
        else
        {
            // A walk-in customer is chosen, never assumed (SALE-05.2): a missing profile is a mistake, and a profile with the walk-in mark is two answers.
            if (walkIn != request.WalkInCustomer)
            {
                invalid.Add("Customer");
            }

            // A request belongs to a profile (BR-SALE-01); the mark has no meaning without one.
            if ((request.PurchaseRequestId is not null && walkIn) || (request.PurchaseRequestId is null && request.TakesRequestInFull is not null))
            {
                invalid.Add("Purchase request");
            }
        }

        var lines = ReadLines(request.Lines, invalid);
        var lotIds = lines.Select(l => l.LotId).ToHashSet();
        var notSoldWhole = (request.NotSoldWholeLotIds ?? []).ToHashSet();
        if (!notSoldWhole.IsSubsetOf(lotIds))
        {
            invalid.Add("Sold the whole lot");
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

        var code = DocumentCodes.Typed(request.Code);
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        var note = ReadNote(request.Note, invalid);
        var payment = request.Payment;
        if (payment is null || !InvoiceMoney.IsWholeAmount(payment.CashAmount) || !Enum.IsDefined(payment.Rest))
        {
            invalid.Add("Payment");
        }
        else
        {
            if (!ordered && walkIn && payment.Rest == SalesInvoiceRestPayment.PayLater)
            {
                // SALE-05.2: Pay later is not offered to a walk-in customer (with an order, the order says who the customer is and the decision checks it).
                invalid.Add("Pay later");
            }

            if (payment.Rest == SalesInvoiceRestPayment.VietQr && code?.Length > MaxVietQrCodeLength)
            {
                // The code is the content of the transfer: the banks cut it at 25 characters.
                invalid.Add("Code");
            }
        }

        if (invalid.Count > 0)
        {
            return (null, invalid.Distinct().ToList());
        }

        return (new SalesInvoiceEntries(
            request.CustomerId,
            request.WalkInCustomer,
            request.ZoneId,
            request.SalesOrderId,
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
            code,
            note), invalid);
    }

    internal static string? ReadNote(string? note, List<string> invalid)
    {
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed?.Length > MaxNoteLength)
        {
            invalid.Add("Note");
        }

        return trimmed;
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

            if (line?.Quantity is { } quantity && !InvoiceMoney.IsKg(quantity))
            {
                invalid.Add($"Line {number} quantity");
            }

            if (line?.UnitPrice is { } price && (price <= 0m || !InvoiceMoney.IsWholeAmount(price)))
            {
                invalid.Add($"Line {number} unit price");
            }

            if (invalid.Count == before)
            {
                lines.Add(new SalesInvoiceLineEntries(
                    number, line!.ProductId!.Value, line.HarvestLotId!.Value, line.Kg!.Value, line.UnitPrice, line.PriceDecidedByOwner, line.SalesOrderLineId, line.UnitId, line.Quantity));
            }
        }

        return lines;
    }
}
