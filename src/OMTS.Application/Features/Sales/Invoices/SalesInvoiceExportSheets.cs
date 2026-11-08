using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The worksheets of SALE-09.2: what the screen shows, text as text (<see cref="ISpreadsheetWriter"/> never reads it as a formula), money and kg as numbers.</summary>
public static class SalesInvoiceExportSheets
{
    public const string ListSheet = "Sales invoices";
    public const string InvoiceSheet = "Invoice";
    public const string LinesSheet = "Lines";
    public const string PaymentsSheet = "Payments";
    public const string SendingsSheet = "Sendings";

    /// <summary>The list, with the total of the Completed invoices of the search as its last row.</summary>
    public static IReadOnlyList<SpreadsheetSheet> List(IReadOnlyList<SalesInvoiceRow> rows, decimal completedTotal, bool withCashAwaiting)
    {
        List<string> headers = ["Code", "Time", "Zone", "Customer", "Total", "Status", "Payment term", "Paid", "Unpaid", "Delivery status"];
        if (withCashAwaiting)
        {
            headers.Add("Cash awaiting confirmation");
        }

        var sheetRows = rows.Select(r =>
        {
            List<object?> cells =
            [
                r.Code, Local(r.InvoicedAt), r.Zone.Name, r.Customer?.Name ?? SalesInvoiceNotifications.WalkInCustomerName, r.Total, r.Status,
                r.PaymentTerm.ToString(), Paid(r.IsPaid), r.UnpaidAmount, r.DeliveryStatus,
            ];
            if (withCashAwaiting)
            {
                cells.Add(r.CashAwaitingConfirmation);
            }

            return (IReadOnlyList<object?>)cells;
        }).ToList();
        sheetRows.Add(["Total of Completed invoices", null, null, null, completedTotal]);
        return [new SpreadsheetSheet(ListSheet, headers, sheetRows)];
    }

    /// <summary>One invoice: its heading, its lines with their amounts down to the total, its payments and its sendings (SALE-09.1 step 2).</summary>
    public static IReadOnlyList<SpreadsheetSheet> Invoice(SalesInvoiceView invoice) =>
    [
        new SpreadsheetSheet(InvoiceSheet, ["Item", "Value"],
        [
            ["Invoice", invoice.Code],
            ["Time", Local(invoice.InvoicedAt)],
            ["Zone", invoice.Zone.Name],
            ["Customer", invoice.Customer?.Name ?? SalesInvoiceNotifications.WalkInCustomerName],
            ["Phone", invoice.Customer?.PhoneNumber],
            ["Status", invoice.Status],
            ["Payment term", invoice.PaymentTerm.ToString()],
            ["Total", invoice.Total],
            ["Paid", Paid(invoice.DebtEffect.IsPaid)],
            ["Unpaid", invoice.DebtEffect.Unpaid],
            ["Delivery status", invoice.DeliveryStatus],
        ]),
        new SpreadsheetSheet(LinesSheet, ["Line", "Product", "Harvest lot", "Kg", "Unit price", "Price source", "Amount"],
        [
            .. invoice.Lines.Select(l => (IReadOnlyList<object?>)[l.LineNumber, l.ProductName, l.LotCode, l.Kg, l.UnitPrice, l.PriceSource.ToString(), l.Amount]),
            ["Subtotal", null, null, invoice.TotalKg, null, null, invoice.Subtotal],
            ["Discount", null, null, null, null, null, invoice.Discount],
            ["Other charges", null, null, null, null, null, invoice.OtherCharges],
            ["Price cuts", null, null, null, null, null, invoice.PriceCutTotal],
            ["Total", null, null, null, null, null, invoice.Total],
        ]),
        new SpreadsheetSheet(PaymentsSheet, ["Code", "Kind", "Method", "Amount", "Date", "Status", "Collected by"],
            invoice.Payments.Select(p => (IReadOnlyList<object?>)[p.Code, p.Kind.ToString(), p.Method.ToString(), p.Amount, p.PaymentDate, p.Status, p.CollectedBy?.Name]).ToList()),
        new SpreadsheetSheet(SendingsSheet, ["Channel", "Delivery status", "Time"],
            invoice.Sendings.Select(s => (IReadOnlyList<object?>)[s.Channel, s.DeliveryStatus, s.DeliveryStatusAt is { } at ? Local(at) : null]).ToList()),
    ];

    private static string? Paid(bool? isPaid) => isPaid switch
    {
        true => "Yes",
        false => "No",
        null => null,
    };

    /// <summary>Business local time, as the writer expects it.</summary>
    private static DateTime Local(DateTimeOffset instant) => instant.ToOffset(BusinessClock.UtcOffset).DateTime;
}
