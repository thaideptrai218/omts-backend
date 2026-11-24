using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Tracing;

/// <summary>
/// TRACE-02.0 step 1: the harvest lot by its lot code (codes starting with it) or through a sales invoice that sells it (not a cancelled one); the harvest day range, the raft code and the
/// product name narrow the lots further. At least one filter is required; all given must match.
/// </summary>
public sealed record LotTraceSearchQuery(string? LotCode, string? InvoiceCode, DateOnly? HarvestFrom = null, DateOnly? HarvestTo = null, string? RaftCode = null, string? ProductName = null);

/// <summary>The reverse trace asks for one seed lot code (PN000123-1, or the receipt code) or a supplier name.</summary>
public sealed record SeedLotTraceQuery(string? Term);

/// <summary>An invoice found through a seed lot: its customer is null when the caller does not view the sales invoices of the invoice's zone (like the prices of the trace).</summary>
public sealed record SeedForwardInvoice(Guid InvoiceId, string InvoiceCode, DateOnly InvoiceDate, string ZoneCode, string? CustomerName, string? CustomerPhone, bool WalkIn, decimal Kg);

/// <summary>A harvest lot that grew from the seed lot, with the invoices that sold it.</summary>
public sealed record SeedForwardLot(LotTraceSearchItem Lot, IReadOnlyList<SeedForwardInvoice> Invoices);

/// <summary>Reverse trace (for recalling a bad seed lot): seed lot or supplier, to the harvest lots in the caller's zones, to the invoices, to the customers.</summary>
public sealed record SeedForwardTrace(IReadOnlyList<SeedForwardLot> Lots, string? Message, string? MessageCode);

/// <summary>The harvest plan a lot was recorded from.</summary>
public sealed record TraceOrderRef(Guid Id, string Code);
public sealed record TraceHarvestPlan(string Code, DateOnly PlannedDate, string Status, IReadOnlyList<TraceOrderRef> Orders);

/// <summary>A quality claim on an invoice of the lot that is still open (awaiting the Owner's response, or an offer the trader has not answered). Shown only; it changes no state.</summary>
public sealed record TraceOpenClaim(string Code, string InvoiceCode, DateTimeOffset SentAt);

/// <summary>A lot to trace, in a zone the caller traces.</summary>
public sealed record LotTraceSearchItem(
    Guid Id, string LotCode, DateTimeOffset HarvestedAt, DateOnly HarvestDate, Guid RaftId, string RaftCode, Guid ZoneId, string ZoneCode, int StatusId, string Status,
    HarvestLotOriginState OriginState);

/// <param name="Lots">The latest harvest first, at most 50.</param>
/// <param name="Message">MSG-GEN-10 when no lot of the caller's zones matches (TRACE-02.0.E1).</param>
public sealed record LotTraceSearchResult(IReadOnlyList<LotTraceSearchItem> Lots, string? Message, string? MessageCode);

/// <summary>A record corrected in Correct traceability data (TRACE-03), marked with the date, the person and the reason of each correction (TRACE-02.0 step 3).</summary>
public sealed record TraceCorrectionMark(DateTimeOffset CorrectedAt, Guid? ActorAccountId, string? ActorName, string? Reason);

/// <summary>A seeding session of the lot's cycle that hung seed on the lane, with its seed lot, purchase receipt and supplier (GBR-04, BR-TRACE-03).</summary>
/// <param name="OnCompletedReceipt">The seed lot is a line of a Completed receipt, not a Draft or Cancelled one (BR-TRACE-01).</param>
public sealed record TraceSeeding(
    Guid SeedingRecordId, DateOnly SeedingDate, decimal Quantity, SeedLotRef SeedLot, int ReceiptStatusId, string ReceiptStatus, bool OnCompletedReceipt,
    IReadOnlyList<TraceCorrectionMark> Corrections);

/// <summary>A lane of the lot and every seeding session of the cycle on it, the earliest first: a lane hung again shows each seed lot (BR-CYCLE-02). None: the lane misses its seed.</summary>
/// <param name="IsFinished">This lot marked the lane finished.</param>
public sealed record TraceLane(int LaneNumber, bool IsFinished, IReadOnlyList<TraceSeeding> Seedings);

/// <param name="UnitPrice">Null when the caller does not view the sales invoices of the invoice's zone (§I.4.4).</param>
/// <param name="Amount">Null in the same case.</param>
public sealed record TraceInvoiceLine(int LineNumber, string ProductName, decimal Kg, decimal? UnitPrice, decimal? Amount);

/// <summary>A sales return of the invoice that stands (CLAIM-03), per outcome, with this lot's kg. A return takes back the whole invoice (BF-05.1).</summary>
public sealed record TraceSalesReturn(Guid Id, string Code, DateOnly ReturnDate, decimal Kg, string Outcome);

/// <summary>A sales invoice that sells the lot, not cancelled, at its latest values, with its trader and the lines of this lot (BR-TRACE-03).</summary>
/// <param name="WalkIn">A walk-in sale: no customer profile, no phone.</param>
/// <param name="Kg">The kg of this lot on the invoice.</param>
public sealed record TraceInvoice(
    Guid InvoiceId, string InvoiceCode, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, Guid ZoneId, string ZoneCode, Guid? CustomerId, string? CustomerName, string? CustomerPhone,
    bool WalkIn, decimal Kg, IReadOnlyList<TraceInvoiceLine> Lines, IReadOnlyList<TraceSalesReturn> Returns);

/// <summary>
/// TRACE-02 Trace lot (SCR-TRACE-01): the lot with its status and publish state, the backward trace (raft, zone, cycle, lanes, seeding sessions, seed lots, receipts, suppliers), what it
/// misses (MSG-TRACE-02, nothing filled in), and the forward trace (invoices, traders, returns). Records corrected in TRACE-03 carry their marks. Viewing changes nothing.
/// </summary>
/// <param name="Corrections">The TRACE-03 corrections of the lot's harvest data.</param>
/// <param name="Missing">The items of MSG-TRACE-02 (the same as MSG-TRACE-01), empty when the backward trace is complete.</param>
/// <param name="Message">MSG-TRACE-02 when something is missing, else null.</param>
/// <param name="Invoices">The oldest invoice first.</param>
/// <param name="InvoicedKg">The kg of the lot on these invoices.</param>
/// <param name="CanPublish">Publish is offered (TRACE-01.2): a Not published lot on an invoice, or a Withdrawn lot for the Owner, in a zone where the caller publishes.</param>
/// <param name="CanCorrect">The link to Correct traceability data (TRACE-03) for the raft: the caller corrects provenance in the zone.</param>
/// <param name="CanExport">The caller exports the trace (TRACE-02.2).</param>
/// <param name="CanWithdraw">Withdraw is offered (TRACE-04): the Cooperative Owner, for a Published lot.</param>
/// <param name="WeighedKg">The kg weighed at the port (the real weight, once the lot has been weighed); <see cref="EstimatedKg"/> is only the estimate made at harvest.</param>
/// <param name="WeighedAt">When it was weighed.</param>
public sealed record LotTrace(
    Guid LotId,
    string LotCode,
    DateTimeOffset HarvestedAt,
    DateOnly HarvestDate,
    decimal CageCount,
    decimal EstimatedKg,
    int StatusId,
    string Status,
    HarvestLotOriginState OriginState,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? WithdrawnAt,
    IReadOnlyList<TraceCorrectionMark> Corrections,
    Guid RaftId,
    string RaftCode,
    Guid ZoneId,
    string ZoneCode,
    string ZoneName,
    Guid CycleId,
    DateOnly CycleStartDate,
    DateOnly? CycleCloseDate,
    int CycleStatusId,
    string CycleStatus,
    IReadOnlyList<TraceLane> Lanes,
    IReadOnlyList<OriginGap> Missing,
    string? Message,
    string? MessageCode,
    IReadOnlyList<TraceInvoice> Invoices,
    decimal InvoicedKg,
    bool CanPublish,
    bool CanCorrect,
    bool CanExport,
    bool CanWithdraw,
    decimal? WeighedKg = null,
    DateTimeOffset? WeighedAt = null,
    TraceHarvestPlan? HarvestPlan = null,
    IReadOnlyList<TraceOpenClaim>? OpenClaims = null);
