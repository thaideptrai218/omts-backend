using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Traceability.Tracing;

/// <summary>
/// TRACE-02 Trace lot (SCR-TRACE-01): a Zone Manager of the lot's zone, the Cooperative Owner, and the Accountant, who views it (§I.4.4 R: LOT_TRACING View; export is
/// LOT_TRACING Export, which the Accountant does not hold). The zone of a lot is the zone of its raft. Traders and other roles are refused. Nothing is written (POST-1).
/// </summary>
public sealed class LotTraceService(
    IHarvestLotRepository lots,
    ISeedingRecordRepository seedings,
    ILotTraceQuery traces,
    IAccountRepository accounts,
    HarvestLotViews views,
    IStatusCatalog statusCatalog,
    ISpreadsheetWriter spreadsheets)
{
    public const int MaxSearchResults = 50;

    /// <summary>
    /// TRACE-02.0 step 1: the lots of the caller's zones that match. No lot code and no invoice code: 400 MSG-GEN-01; no match in the caller's zones (a lot of another zone included):
    /// MSG-GEN-10 with an empty list (TRACE-02.0.E1); no right to trace in any zone: 403.
    /// </summary>
    public async Task<Result<LotTraceSearchResult>> SearchAsync(LotTraceSearchQuery query, CancellationToken cancellationToken)
    {
        var zones = (await views.AccessAsync(cancellationToken))?.PermittedZonesFor(PermissionFunctions.LotTracing, PermissionAction.View) ?? PermittedZones.None;
        if (zones.IsEmpty)
        {
            return AppError.Forbidden;
        }

        var lotCode = Trimmed(query.LotCode);
        var invoiceCode = Trimmed(query.InvoiceCode);
        if (lotCode is null && invoiceCode is null)
        {
            return AppError.Invalid("Lot code");
        }

        var found = await traces.SearchAsync(lotCode, invoiceCode, zones, MaxSearchResults, cancellationToken);
        var items = found.Select(l => new LotTraceSearchItem(
                l.Id, l.LotCode, l.HarvestedAt, l.HarvestDate, l.RaftId, l.Raft.Code, l.Raft.ZoneId, l.Raft.Zone.Code, l.StatusId, statusCatalog.DisplayNameOf(l.StatusId), l.OriginState))
            .ToList();
        var empty = items.Count == 0 ? UserMessages.NoDataForFilters(invoiceCode is null ? "lot code" : lotCode is null ? "sales invoice" : "lot code and sales invoice") : null;
        return new LotTraceSearchResult(items, empty?.Text, empty?.Code);
    }

    /// <summary>TRACE-02.0 steps 2-3 with TRACE-02.1: 404 for an unknown lot, 403 MSG-GEN-08 outside the caller's zones.</summary>
    public Task<Result<LotTrace>> TraceAsync(Guid lotId, CancellationToken cancellationToken) => TraceAsync(lotId, PermissionAction.View, cancellationToken);

    /// <summary>TRACE-02.2: the same trace as a file (LOT_TRACING Export in the lot's zone); 500 MSG05 when the file cannot be written.</summary>
    public async Task<Result<ExportedFile>> ExportAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var trace = await TraceAsync(lotId, PermissionAction.Export, cancellationToken);
        if (!trace.IsSuccess)
        {
            return trace.Error!;
        }

        try
        {
            return new ExportedFile(LotTraceExport.FileName(trace.Value!.LotCode), ISpreadsheetWriter.ContentType, spreadsheets.Write(LotTraceExport.Sheets(trace.Value)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private async Task<Result<LotTrace>> TraceAsync(Guid lotId, PermissionAction action, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        var access = await views.AccessAsync(cancellationToken);
        if (!LotTraceBuilder.Allows(access, PermissionFunctions.LotTracing, action, lot.Raft.ZoneId))
        {
            return AppError.Forbidden;
        }

        // One query each, whatever the number of lanes, sessions and invoices.
        var sessions = await seedings.ListByCycleAsync(lot.CycleId, cancellationToken);
        var lines = await traces.SaleLinesAsync(lot.Id, cancellationToken);
        var corrections = await traces.CorrectionsAsync([lot.Id, .. sessions.Select(s => s.Id)], cancellationToken);
        var actors = corrections.Where(c => c.ActorAccountId is not null).Select(c => c.ActorAccountId!.Value).Distinct().ToList();
        var names = await accounts.GetDisplayNamesAsync(actors, cancellationToken);
        var returns = await traces.SaleReturnsAsync(lot.Id, cancellationToken);
        return LotTraceBuilder.Build(new LotTraceSource(lot, sessions, lines, corrections, names, returns), access, statusCatalog);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
