using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Issues;

/// <summary>The movements of an item ("Nhập – xuất" tab) in a period, newest first. Reversal rows (an edit or cancellation undoing a row) and the rows they undo are left out unless asked for.</summary>
public sealed record InputMovementQuery(DateOnly? From = null, DateOnly? To = null, InputStockMovementKind? Kind = null, bool IncludeReversals = false, int Page = 1, int PageSize = 20);

/// <summary>
/// Reads of the movement ledger of an item: its movements with the running balance (worked out in the order of time then write order, so a document dated in the past stays consistent), and
/// the seed lots of a Seed item with the quantity left of each and the quantity that has no lot. Readable by whoever reads the item catalogue.
/// </summary>
public sealed class InputMovementService(
    IFarmingInputItemRepository items, IInputStockRepository stock, IInputMovementQuery movements, InputItemAccess access, ISpreadsheetWriter spreadsheets, TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    public async Task<Result<InputMovementPage>> MovementsAsync(Guid itemId, InputMovementQuery query, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(itemId, query, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        var (item, rows) = read.Value;
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        return new InputMovementPage(rows.Skip((page - 1) * pageSize).Take(pageSize).Select(View).ToList(), page, pageSize, rows.Count, item.Unit);
    }

    public async Task<Result<ExportedFile>> ExportAsync(Guid itemId, InputMovementQuery query, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(itemId, query, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        var (item, rows) = read.Value;
        string[] headers = ["Document", "Type", "Time", "Party", "Seed lot", "Cost", "Price", "Quantity", "Balance", "Unit"];
        var data = rows.Select(r => (IReadOnlyList<object?>)new List<object?>
        {
            r.Movement.DocumentCode, r.Movement.Kind.ToString(), r.Movement.OccurredAt.ToOffset(BusinessClock.UtcOffset).DateTime, r.Movement.PartyName, r.SeedLotCode,
            r.Movement.UnitCost, r.Movement.TransactionPrice, r.Movement.Quantity, r.BalanceAfter, item.Unit,
        }).ToList();
        try
        {
            return new ExportedFile($"{item.Code}-movements-{timeProvider.Today():yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, spreadsheets.Write([new SpreadsheetSheet("Movements", headers, data)]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    /// <summary>The seed lots of a Seed item, oldest first, with the quantity of each on the ledger and the quantity that has no lot.</summary>
    public async Task<Result<InputSeedLotsView>> SeedLotsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var item = (await items.GetByIdsAsync([itemId], cancellationToken)).FirstOrDefault();
        if (item is null || item.IsHidden)
        {
            return AppError.NotFound;
        }

        if (item.Group != InputItemGroup.Seed)
        {
            return AppError.Invalid("Item", "Only a seed item has seed lots.");
        }

        var lots = await movements.SeedLotsAsync(itemId, cancellationToken);
        var onHand = (await stock.OnHandAsync([itemId], cancellationToken)).GetValueOrDefault(itemId);
        var suggested = lots.FirstOrDefault(l => l.OnHand > 0)?.LineId;
        return new InputSeedLotsView(
            lots.Select(l => new InputSeedLotView(l.LineId, l.Code, l.ReceiptId, l.ReceiptCode, l.ReceivedAt, l.SupplierName, l.SupplierLotCode, l.ReceivedQuantity, l.OnHand)).ToList(),
            onHand - lots.Sum(l => l.OnHand), suggested, item.Unit);
    }

    private static InputMovementView View(InputMovementRow r)
    {
        var m = r.Movement;
        return new InputMovementView(
            m.Id, m.Sequence, m.Kind, m.DocumentId, m.DocumentCode, m.OccurredAt, m.Quantity, r.BalanceAfter, m.UnitCost, m.TransactionPrice, m.PartyName, m.SeedLotLineId, r.SeedLotCode, m.ReversesMovementId,
            r.Source?.Kind, r.Source?.RecordId, r.Source?.CycleId, r.Source?.RaftId, r.Source?.ZoneId);
    }

    /// <summary>The movements that match the query, newest first, each with the balance after it in time order.</summary>
    private async Task<Result<(FarmingInputItem Item, IReadOnlyList<InputMovementRow> Rows)>> ReadAsync(Guid itemId, InputMovementQuery query, CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (query.From > query.To)
        {
            return AppError.Invalid("Period");
        }

        if (query.Kind is { } kind && !Enum.IsDefined(kind))
        {
            return AppError.Invalid("Kind");
        }

        var item = (await items.GetByIdsAsync([itemId], cancellationToken)).FirstOrDefault();
        if (item is null || item.IsHidden)
        {
            return AppError.NotFound;
        }

        var from = query.From is { } f ? BusinessClock.FromLocal(f.ToDateTime(TimeOnly.MinValue)) : (DateTimeOffset?)null;
        var before = query.To is { } t ? BusinessClock.FromLocal(t.AddDays(1).ToDateTime(TimeOnly.MinValue)) : (DateTimeOffset?)null;
        var all = await movements.ForItemAsync(itemId, query.IncludeReversals, cancellationToken);
        // An adjustment counts as the kind of the row it undoes, so the kind filter keeps the adjustments of that kind when they are shown.
        var kindOf = all.ToDictionary(r => r.Movement.Id, r => r.Movement.Kind);
        bool OfKind(InputStockMovement m) =>
            query.Kind is null || m.Kind == query.Kind || (m.Kind == InputStockMovementKind.Reversal && m.ReversesMovementId is { } undone && kindOf.TryGetValue(undone, out var original) && original == query.Kind);
        var rows = all
            .Where(r => (from is null || r.Movement.OccurredAt >= from) && (before is null || r.Movement.OccurredAt < before) && OfKind(r.Movement))
            .OrderByDescending(r => r.Movement.OccurredAt).ThenByDescending(r => r.Movement.Sequence).ToList();
        return (item, rows);
    }
}

/// <summary>
/// Deleting an item or one of its seed lots: whatever is left is written off by a Completed disposal (reason "Xóa vật tư" / "Xóa lô giống") in the same save, so the history stays whole. A deleted
/// item is hidden from every list and screen; its code and name stay taken, and its documents still name it. Owner only (API filter).
/// </summary>
public sealed class FarmingInputItemDeletionService(
    IFarmingInputItemRepository items, IInputStockRepository stock, IInputMovementQuery movements, InputIssueService issueService, AuditLogService audit, IUnitOfWork unitOfWork)
{
    public const string ItemReason = "Xóa vật tư";
    public const string LotReason = "Xóa lô giống";

    public async Task<Result<InputItemDeleted>> DeleteItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await items.GetForEditAsync(itemId, cancellationToken);
        if (item is not { IsHidden: false })
        {
            return AppError.NotFound;
        }

        var buckets = await stock.BucketBalancesAsync([item.Id], cancellationToken);
        var parts = buckets.Where(b => b.Value > 0).OrderBy(b => b.Key.LotId).Select(b => (item, b.Key.LotId, b.Value)).ToList();
        InputIssue? disposal = null;
        if (parts.Count > 0)
        {
            disposal = await issueService.RecordDisposalAsync(parts, ItemReason, cancellationToken);
        }

        item.IsHidden = true;
        unitOfWork.MarkChanged(item);
        audit.Record(new AuditEntry("FarmingInputItem.Deleted", FarmingInputItemService.RecordType)
        {
            RecordId = item.Id, RecordCode = item.Code, Before = new { item.Code, item.Name, WrittenOff = parts.Sum(p => p.Value) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new InputItemDeleted(disposal?.Id, disposal?.Code, parts.Sum(p => p.Value));
    }

    /// <summary>Writes off what is left of a seed lot; 400 when nothing is left.</summary>
    public async Task<Result<InputItemDeleted>> DeleteSeedLotAsync(Guid itemId, Guid lotLineId, CancellationToken cancellationToken)
    {
        var item = await items.GetForEditAsync(itemId, cancellationToken);
        if (item is not { IsHidden: false })
        {
            return AppError.NotFound;
        }

        var lot = item.Group == InputItemGroup.Seed ? await movements.SeedLotAsync(item.Id, lotLineId, cancellationToken) : null;
        if (lot is null)
        {
            return AppError.NotFound;
        }

        if (lot.OnHand <= 0)
        {
            return AppError.Invalid("Seed lot", "Nothing is left of this lot.");
        }

        var disposal = await issueService.RecordDisposalAsync([(item, (Guid?)lot.LineId, lot.OnHand)], $"{LotReason} {lot.Code}", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new InputItemDeleted(disposal.Id, disposal.Code, lot.OnHand);
    }
}

/// <summary>The types of a use issue ("Loại xuất"): three system types are seeded and never change (MSG-INPUT-02); the Owner adds others, edits them, and deletes one no issue has, or hides it. Reading follows the item catalogue; writes are Owner only (API filter).</summary>
public sealed class InputIssueTypeService(
    IInputIssueTypeRepository types, InputItemAccess access, RowVersionGuard rowVersions, AuditLogService audit, IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(InputIssueType);
    private const string ObjectName = "Issue type";
    public const int MaxNameLength = 100;

    public async Task<Result<IReadOnlyList<InputIssueTypeView>>> ListAsync(CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        return Result<IReadOnlyList<InputIssueTypeView>>.Success((await types.ListActiveAsync(cancellationToken)).Select(View).ToList());
    }

    public async Task<Result<InputIssueTypeView>> CreateAsync(SaveInputIssueTypeRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return AppError.InvalidFields(["Name"]);
        }

        if (await types.FindByNameAsync(name, null, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        var type = new InputIssueType { Name = name };
        types.Add(type);
        audit.Record(new AuditEntry("InputIssueType.Created", RecordType) { RecordId = type.Id, RecordCode = name, After = new { type.Name } });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(type);
    }

    public async Task<Result<InputIssueTypeView>> UpdateAsync(Guid typeId, SaveInputIssueTypeRequest request, CancellationToken cancellationToken)
    {
        var type = await types.GetForEditAsync(typeId, cancellationToken);
        if (type is null || !type.IsActive)
        {
            return AppError.NotFound;
        }

        if (type.IsSystem)
        {
            return AppError.Conflict(UserMessages.InputSystemIssueType);
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return AppError.InvalidFields(["Name"]);
        }

        if (await rowVersions.CheckAsync(type, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (await types.FindByNameAsync(name, type.Id, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        if (name != type.Name)
        {
            audit.Record(new AuditEntry("InputIssueType.Edited", RecordType) { RecordId = type.Id, RecordCode = name, Before = new { type.Name }, After = new { Name = name } });
            type.Name = name;
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return View(type);
    }

    /// <summary>Deletes a type no issue has, otherwise hides it; either way it is no longer listed.</summary>
    public async Task<Result<Done>> DeleteAsync(Guid typeId, CancellationToken cancellationToken)
    {
        var type = await types.GetForEditAsync(typeId, cancellationToken);
        if (type is null || !type.IsActive)
        {
            return AppError.NotFound;
        }

        if (type.IsSystem)
        {
            return AppError.Conflict(UserMessages.InputSystemIssueType);
        }

        if (await types.IsUsedAsync(type.Id, cancellationToken))
        {
            type.IsActive = false;
            unitOfWork.MarkChanged(type);
            audit.Record(new AuditEntry("InputIssueType.Hidden", RecordType) { RecordId = type.Id, RecordCode = type.Name, Before = new { IsActive = true }, After = new { IsActive = false } });
        }
        else
        {
            types.Remove(type);
            audit.Record(new AuditEntry("InputIssueType.Deleted", RecordType) { RecordId = type.Id, RecordCode = type.Name, Before = new { type.Name } });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    private static InputIssueTypeView View(InputIssueType t) => new(t.Id, t.Name, t.IsSystem, RowVersionGuard.Encode(t.RowVersion));
}
