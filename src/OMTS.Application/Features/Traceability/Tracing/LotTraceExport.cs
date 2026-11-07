using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;

namespace OMTS.Application.Features.Traceability.Tracing;

/// <summary>
/// TRACE-02.2: the trace result shown, as a file: sheet Lot (the lot, its raft, zone and cycle, what is missing, its corrections), sheet Backward (one row per lane and seeding session),
/// sheet Forward (one row per invoice line). Times and dates are Vietnam local; prices only where the screen shows them.
/// </summary>
public static class LotTraceExport
{
    public const string LotSheet = "Lot";
    public const string BackwardSheet = "Backward";
    public const string ForwardSheet = "Forward";

    public static readonly IReadOnlyList<string> LotHeaders =
    [
        "Lot code", "Harvested at", "Harvest date", "Zone", "Raft", "Cycle start", "Cycle close", "Cycle status", "Lanes", "Cages", "Estimated kg", "Status",
        "Origin information", "Published at", "Withdrawn at", "Invoiced kg", "Missing data", "Corrections",
    ];

    public static readonly IReadOnlyList<string> BackwardHeaders =
    [
        "Lane", "Finished in this lot", "Seeding date", "Quantity", "Unit", "Seed lot", "Seed", "Purchase receipt", "Date received", "Receipt status", "Supplier", "Missing data",
        "Corrections",
    ];

    public static readonly IReadOnlyList<string> ForwardHeaders =
    [
        "Invoice", "Invoiced at", "Invoice date", "Invoice zone", "Customer", "Phone number", "Line", "Product", "Kg", "Unit price", "Amount", "Sales returns",
    ];

    public static IReadOnlyList<SpreadsheetSheet> Sheets(LotTrace trace) =>
    [
        new SpreadsheetSheet(LotSheet, LotHeaders, [LotRow(trace)]),
        new SpreadsheetSheet(BackwardSheet, BackwardHeaders, trace.Lanes.SelectMany(lane => BackwardRows(trace, lane)).ToList()),
        new SpreadsheetSheet(ForwardSheet, ForwardHeaders, trace.Invoices.SelectMany(ForwardRows).ToList()),
    ];

    /// <summary>trace-{lotCode}.xlsx with the code reduced to file-name-safe characters.</summary>
    public static string FileName(string lotCode) =>
        $"trace-{new string(lotCode.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray())}.xlsx";

    /// <summary>"dd/MM/yyyy HH:mm Name: reason" for each correction, joined with "; ".</summary>
    public static string? Corrections(IReadOnlyList<TraceCorrectionMark> marks) => marks.Count == 0
        ? null
        : string.Join("; ", marks.Select(m => $"{Local(m.CorrectedAt).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} {m.ActorName ?? "System"}: {m.Reason}"));

    private static IReadOnlyList<object?> LotRow(LotTrace t) =>
    [
        t.LotCode, Local(t.HarvestedAt), Day(t.HarvestDate), $"{t.ZoneCode} - {t.ZoneName}", t.RaftCode, Day(t.CycleStartDate), t.CycleCloseDate is { } close ? Day(close) : null,
        t.CycleStatus, string.Join(", ", t.Lanes.Select(l => l.LaneNumber)), t.CageCount, t.EstimatedKg, t.Status, t.OriginState.ToString(),
        t.PublishedAt is { } published ? Local(published) : null, t.WithdrawnAt is { } withdrawn ? Local(withdrawn) : null, t.InvoicedKg,
        t.Missing.Count == 0 ? null : string.Join("; ", t.Missing.Select(m => m.Describe())), Corrections(t.Corrections),
    ];

    private static IEnumerable<IReadOnlyList<object?>> BackwardRows(LotTrace trace, TraceLane lane)
    {
        var missing = trace.Missing.Where(m => m.LaneNumber == lane.LaneNumber).Select(m => m.Describe()).ToList();
        var missingText = missing.Count == 0 ? null : string.Join("; ", missing);
        if (lane.Seedings.Count == 0)
        {
            yield return [lane.LaneNumber, lane.IsFinished, null, null, null, null, null, null, null, null, null, missingText, null];
            yield break;
        }

        foreach (var s in lane.Seedings)
        {
            yield return
            [
                lane.LaneNumber, lane.IsFinished, Day(s.SeedingDate), s.Quantity, s.SeedLot.Unit, s.SeedLot.Code, s.SeedLot.ItemName, s.SeedLot.ReceiptCode, Day(s.SeedLot.ReceivedDate),
                s.ReceiptStatus, s.SeedLot.SupplierName, missingText, Corrections(s.Corrections),
            ];
        }
    }

    private static IEnumerable<IReadOnlyList<object?>> ForwardRows(TraceInvoice invoice) => invoice.Lines.Select(line => (IReadOnlyList<object?>)
    [
        invoice.InvoiceCode, Local(invoice.InvoicedAt), Day(invoice.InvoiceDate), invoice.ZoneCode, invoice.WalkIn ? "Walk-in customer" : invoice.CustomerName, invoice.CustomerPhone,
        line.LineNumber, line.ProductName, line.Kg, line.UnitPrice, line.Amount, invoice.Returns.Count == 0 ? null : string.Join(", ", invoice.Returns.Select(r => r.Code)),
    ]);

    private static DateTime Local(DateTimeOffset at) => at.ToOffset(BusinessClock.UtcOffset).DateTime;

    private static DateTime Day(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);
}
