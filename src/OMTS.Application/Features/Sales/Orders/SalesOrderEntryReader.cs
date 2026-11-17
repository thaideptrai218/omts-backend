using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>One line as read: the product, the unit of the line, the quantity, and the price entered (null: the list price of the unit).</summary>
internal sealed record SalesOrderLineEntries(int LineNumber, Guid ProductId, Guid UnitId, decimal Quantity, decimal? UnitPrice, bool PriceDecidedByOwner, string? Note);

/// <summary>The advance of a new order once its shape is right; what it names is checked against the database after.</summary>
internal sealed record SalesOrderAdvanceEntries(
    decimal Amount, SalesOrderAdvanceMethod Method, Guid? CashAccountId, string? BankReference, Guid? CollectedById, DateTimeOffset? PaidAt, string? Note, string? DeduplicationKey,
    bool ConfirmDuplicateBankReference);

/// <summary>The fields of an order once their shapes are right; what they name is checked against the database after.</summary>
internal sealed record SalesOrderEntries(
    string? Code,
    Guid? CustomerId,
    Guid? PurchaseRequestId,
    SalesOrderSource? Source,
    Guid? ReceivedById,
    DateTimeOffset? OrderedAt,
    DateTimeOffset? PickupAt,
    string? Note,
    decimal Discount,
    bool DiscountDecidedByOwner,
    IReadOnlyList<SalesOrderLineEntries> Lines,
    SalesOrderAdvanceEntries? Advance,
    string? DeduplicationKey,
    string? RowVersion)
{
    public bool IsWalkIn => CustomerId is null;
}

/// <summary>The shape of each entry of an order (MSG-GEN-01 names every field to fix): no gap in the lines, a quantity above 0 with at most 3 decimals, whole VND amounts.</summary>
internal static class SalesOrderEntryReader
{
    /// <param name="isUpdate">An update replaces the order and needs the row version; an advance on it is a new payment added in the same save.</param>
    public static (SalesOrderEntries? Entries, List<string> Invalid) Read(SaveSalesOrderRequest request, bool isUpdate)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        // A walk-in customer is chosen, never assumed: a missing profile is a mistake, and a profile with the walk-in mark is two answers.
        var walkIn = request.CustomerId is null;
        if (walkIn != request.WalkInCustomer)
        {
            invalid.Add("Customer");
        }

        // A request belongs to a profile: a walk-in buyer has none, and an update never changes the request the order came from.
        if (request.PurchaseRequestId is not null && (walkIn || isUpdate))
        {
            invalid.Add("Purchase request");
        }

        var source = request.Source;
        if (request.PurchaseRequestId is not null)
        {
            if (source is not (null or SalesOrderSource.App))
            {
                invalid.Add("Source");
            }

            source = SalesOrderSource.App;
        }
        else if (source is not { } named || !Enum.IsDefined(named))
        {
            invalid.Add("Source");
        }

        if (!InvoiceMoney.IsWholeAmount(request.Discount))
        {
            invalid.Add("Discount");
        }

        if (request.Note?.Trim().Length > SalesOrderRules.MaxNoteLength)
        {
            invalid.Add("Note");
        }

        var key = string.IsNullOrWhiteSpace(request.DeduplicationKey) ? null : request.DeduplicationKey.Trim();
        if (key?.Length > SalesOrderRules.MaxKeyLength)
        {
            invalid.Add("Deduplication key");
        }

        var lines = ReadLines(request.Lines, invalid);
        var advance = ReadAdvance(request.Advance, walkIn, invalid);
        if (isUpdate && request.RowVersion is null)
        {
            invalid.Add("RowVersion");
        }

        if (invalid.Count > 0)
        {
            return (null, invalid.Distinct().ToList());
        }

        return (new SalesOrderEntries(
            DocumentCodes.Typed(request.Code), request.CustomerId, request.PurchaseRequestId, source, request.ReceivedById, request.OrderedAt, request.PickupAt,
            string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(), request.Discount, request.DiscountDecidedByOwner, lines, advance, key, request.RowVersion), invalid);
    }

    private static List<SalesOrderLineEntries> ReadLines(IReadOnlyList<SalesOrderLineInput?>? entries, List<string> invalid)
    {
        var lines = new List<SalesOrderLineEntries>();
        if (entries is null || entries.Count == 0 || entries.Count > SalesOrderRules.MaxLines)
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

            if (line?.UnitId is null)
            {
                invalid.Add($"Line {number} unit");
            }

            if (!InvoiceMoney.IsKg(line?.Quantity))
            {
                invalid.Add($"Line {number} quantity");
            }

            if (line?.UnitPrice is { } price && (price <= 0m || !InvoiceMoney.IsWholeAmount(price)))
            {
                invalid.Add($"Line {number} unit price");
            }

            if (line?.Note?.Trim().Length > SalesOrderRules.MaxLineNoteLength)
            {
                invalid.Add($"Line {number} note");
            }

            if (invalid.Count == before)
            {
                lines.Add(new SalesOrderLineEntries(
                    number, line!.ProductId!.Value, line.UnitId!.Value, line.Quantity!.Value, line.UnitPrice, line.PriceDecidedByOwner,
                    string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim()));
            }
        }

        return lines;
    }

    /// <summary>The advance of a new order, of an update of an order, or of the advance endpoint: a new payment each time.</summary>
    internal static SalesOrderAdvanceEntries? ReadAdvance(SalesOrderAdvanceInput? advance, bool walkIn, List<string> invalid)
    {
        if (advance is null)
        {
            return null;
        }

        var before = invalid.Count;
        // A walk-in buyer has no profile to hold the money.
        if (walkIn)
        {
            invalid.Add("Advance");
            return null;
        }

        if (!InvoiceMoney.IsWholeAmount(advance.Amount) || advance.Amount is not > 0m)
        {
            invalid.Add("Advance amount");
        }

        if (advance.Method is not { } method || !Enum.IsDefined(method))
        {
            invalid.Add("Advance method");
        }
        else if (method == SalesOrderAdvanceMethod.VietQr && advance.CashAccountId is null)
        {
            invalid.Add("Cash account");
        }

        return invalid.Count > before
            ? null
            : new SalesOrderAdvanceEntries(
                advance.Amount!.Value, advance.Method!.Value, advance.CashAccountId, advance.BankReference, advance.CollectedById, advance.PaidAt, advance.Note, advance.DeduplicationKey,
                advance.ConfirmDuplicateBankReference);
    }
}
