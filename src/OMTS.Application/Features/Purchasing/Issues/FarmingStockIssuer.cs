using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Issues;

/// <summary>Where a farming record used the stock: the zone, the raft when it has one, and the label the ledger shows (zone and raft code, or the zone code).</summary>
public sealed record StockPlace(Guid ZoneId, Guid? RaftId, string Label);

/// <summary>One quantity a farming record takes out of stock: in the base unit of the item, from its seed lot for a seed item.</summary>
public sealed record FarmingIssueLine(FarmingInputItem Item, Guid? SeedLotLineId, decimal Quantity);

/// <summary>
/// Stock is the single source of the quantity on hand (PM decision 11/11/2026): a seeding session (CYCLE-02) and a material usage (CYCLE-03) post a Completed use issue (Xuất dùng) in the same
/// save, so the seed or material leaves the stock once and is never counted twice. The record names its issue and the issue names the record, its raft and its cycle. Changing or removing the
/// record undoes the issue's ledger rows (reversal rows) and writes the new ones, or cancels the issue. The issue is owned by the record: the issue screens refuse to change it.
/// MSG-INPUT-01 is answered by <see cref="CheckNotNegativeAsync"/> when the cooperative does not allow a negative stock.
/// </summary>
public sealed class FarmingStockIssuer(
    IInputIssueRepository issues,
    InputIssueCodeService codes,
    InputStockLedger ledger,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(InputIssue);

    /// <summary>Adds a Completed use issue for the record and writes its rows on the ledger. Nothing is saved: the caller saves the record and the issue together.</summary>
    /// <param name="typeId">The system type of the issue: seeding or raft repair.</param>
    public Task<InputIssue> PostAsync(
        InputIssueSource source, Guid sourceRecordId, Raft raft, Guid? cycleId, DateOnly date, IReadOnlyList<FarmingIssueLine> lines, Guid typeId, CancellationToken cancellationToken) =>
        PostAsync(source, sourceRecordId, PlaceOf(raft), cycleId, date, lines, typeId, cancellationToken);

    /// <summary>The same for a record that belongs to a zone and no raft, as a nursery batch does: the issue holds the zone only.</summary>
    public async Task<InputIssue> PostAsync(
        InputIssueSource source, Guid sourceRecordId, StockPlace place, Guid? cycleId, DateOnly date, IReadOnlyList<FarmingIssueLine> lines, Guid typeId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var issue = new InputIssue
        {
            Code = await codes.NextAsync(InputIssueKind.Use, cancellationToken),
            Kind = InputIssueKind.Use,
            IssuedAt = At(date),
            ZoneId = place.ZoneId,
            RaftId = place.RaftId,
            IssueTypeId = typeId,
            StatusId = StatusIds.InputIssue.Completed,
            CompletedAt = now,
            SourceKind = source,
            SourceRecordId = sourceRecordId,
            CycleId = cycleId,
        };
        issues.Add(issue);
        AddLines(issue, lines);
        await PostRowsAsync(issue, place, cancellationToken);
        audit.Record(new AuditEntry("InputIssue.Created", RecordType) { RecordId = issue.Id, RecordCode = issue.Code, ZoneId = place.ZoneId, After = Snapshot(issue) });
        return issue;
    }

    /// <summary>Undoes the rows of the issue and writes them again for the new date and lines. The issue must be tracked with its lines.</summary>
    public Task ReplaceAsync(InputIssue issue, Raft raft, Guid? cycleId, DateOnly date, IReadOnlyList<FarmingIssueLine> lines, CancellationToken cancellationToken) =>
        ReplaceAsync(issue, PlaceOf(raft), cycleId, date, lines, cancellationToken);

    public async Task ReplaceAsync(InputIssue issue, StockPlace place, Guid? cycleId, DateOnly date, IReadOnlyList<FarmingIssueLine> lines, CancellationToken cancellationToken)
    {
        var before = Snapshot(issue);
        await ledger.ReverseAsync(issue.Id, afterReceiptRemoved: false, cancellationToken);
        foreach (var line in issue.Lines.ToList())
        {
            issue.Lines.Remove(line);
            issues.RemoveLine(line);
        }

        issue.IssuedAt = At(date);
        (issue.RaftId, issue.ZoneId, issue.CycleId) = (place.RaftId, place.ZoneId, cycleId);
        AddLines(issue, lines);
        unitOfWork.MarkChanged(issue);
        await PostRowsAsync(issue, place, cancellationToken);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(issue));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("InputIssue.Edited", RecordType) { RecordId = issue.Id, RecordCode = issue.Code, ZoneId = place.ZoneId, Before = changedBefore, After = changedAfter });
        }
    }

    /// <summary>The record is removed: its quantities return to the stock and the issue is Cancelled (it counts nowhere and keeps the history).</summary>
    public Task CancelAsync(InputIssue issue, Raft raft, string reason, CancellationToken cancellationToken) => CancelAsync(issue, raft.ZoneId, reason, cancellationToken);

    public async Task CancelAsync(InputIssue issue, Guid zoneId, string reason, CancellationToken cancellationToken)
    {
        await ledger.ReverseAsync(issue.Id, afterReceiptRemoved: false, cancellationToken);
        var from = issue.StatusId;
        issue.StatusId = StatusIds.InputIssue.Cancelled;
        issue.CancelledAt = timeProvider.GetUtcNow();
        issue.CancelReason = reason.Length > 1000 ? reason[..1000] : reason;
        unitOfWork.MarkChanged(issue);
        audit.RecordStatusChange(RecordType, issue.Id, issue.Code, from, StatusIds.InputIssue.Cancelled, issue.CancelReason, zoneId);
    }

    /// <summary>MSG-INPUT-01 for the first item or seed lot the posted issues would take below zero, unless the cooperative allows it. Call it before saving, and save nothing when it answers.</summary>
    public Task<AppError?> CheckNotNegativeAsync(CancellationToken cancellationToken) => ledger.CheckNotNegativeAsync(cancellationToken);

    public Task<bool> NegativeAllowedAsync(CancellationToken cancellationToken) => ledger.AllowNegativeAsync(cancellationToken);

    /// <summary>The business time the issue is dated: noon of the day of the record, so it falls after a receipt of the same day.</summary>
    private static DateTimeOffset At(DateOnly date) => new(date.ToDateTime(new TimeOnly(12, 0)), BusinessClock.UtcOffset);

    private void AddLines(InputIssue issue, IReadOnlyList<FarmingIssueLine> lines)
    {
        var number = 0;
        foreach (var input in lines)
        {
            // Quantities are in the base unit of the item, so the factor is 1 and the cost is the cost of the item per base unit.
            var unitCost = decimal.Round(input.Item.CostPrice ?? 0m, 4);
            var line = new InputIssueLine
            {
                IssueId = issue.Id,
                LineNumber = ++number,
                ItemId = input.Item.Id,
                SeedLotLineId = input.SeedLotLineId,
                UnitFactor = 1m,
                Unit = input.Item.Unit,
                EnteredQuantity = input.Quantity,
                Quantity = input.Quantity,
                UnitCost = unitCost,
                Value = PurchaseMoney.RoundHalfUp(input.Quantity * unitCost),
            };
            issue.Lines.Add(line);
            issues.AddLine(line);
        }
    }

    private static StockPlace PlaceOf(Raft raft) => new(raft.ZoneId, raft.Id, $"{raft.Zone?.Code} · {raft.Code}");

    private async Task PostRowsAsync(InputIssue issue, StockPlace place, CancellationToken cancellationToken) =>
        await ledger.PostIssueAsync(
            InputStockMovementKind.Issue, issue.Id, issue.Code, issue.IssuedAt, place.Label,
            issue.Lines.OrderBy(l => l.LineNumber).Select(l => new StockPosting(l.ItemId, l.SeedLotLineId, -l.Quantity, null)).ToList(), cancellationToken);

    private static object Snapshot(InputIssue i) => new
    {
        i.Code, i.Kind, i.IssuedAt, i.ZoneId, i.RaftId, i.IssueTypeId, i.SourceKind, i.SourceRecordId, i.CycleId,
        Lines = i.Lines.OrderBy(l => l.LineNumber).Select(l => new { l.LineNumber, l.ItemId, Quantity = l.EnteredQuantity, l.SeedLotLineId, l.UnitCost, l.Value }).ToList(),
    };
}
