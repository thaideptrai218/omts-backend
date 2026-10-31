using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>
/// PURCHASE-08 – the Cooperative Owner's catalogue of farming input items (writes are Owner only, API filter). Code and
/// name are unique, discontinued items included (GBR-03, BR-PURCHASE-10); all Seed items share one unit; group and unit
/// are fixed once the item is used; only an In Use item is edited; a Discontinued item can be resumed (GBR-01).
/// </summary>
public sealed class FarmingInputItemService(
    IFarmingInputItemRepository items,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(FarmingInputItem);
    private const string ObjectName = "Farming input item";

    /// <summary>Readable by whoever views receipts, supplier payments or cultivation cycles (pickers, filters).</summary>
    public async Task<Result<IReadOnlyList<FarmingInputItemView>>> ListAsync(
        string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken)
    {
        if (!await MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var found = await items.ListAsync(search, group, statusId, cancellationToken);
        var used = await items.UsedAmongAsync(found.Select(i => i.Id).ToList(), cancellationToken);
        return found.Select(i => View(i, used.Contains(i.Id))).ToList();
    }

    public async Task<Result<FarmingInputItemView>> GetAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        return await items.GetForEditAsync(itemId, cancellationToken) is { } item
            ? View(item, await items.IsUsedAsync(item.Id, cancellationToken))
            : AppError.NotFound;
    }

    /// <summary>The proposed code (VT001, VT002…; GBR-03) and the unit of the Seed items (BR-PURCHASE-10) for a new item.</summary>
    public async Task<NewFarmingInputItemProposal> ProposeAsync(CancellationToken cancellationToken) => new(
        FarmingInputItemCodes.Next(await items.CodesStartingWithAsync(FarmingInputItemCodes.Prefix, cancellationToken)),
        await items.SeedUnitAsync(null, cancellationToken));

    public async Task<Result<FarmingInputItemView>> CreateAsync(SaveFarmingInputItemRequest request, CancellationToken cancellationToken)
    {
        var (values, invalid) = Validate(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await CheckSeedUnitAsync(values, null, cancellationToken) is { } seedUnit)
        {
            return seedUnit;
        }

        if (await DuplicateAsync(values, null, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        var item = new FarmingInputItem();
        Apply(item, values);
        items.Add(item);
        audit.Record(new AuditEntry("FarmingInputItem.Created", RecordType) { RecordId = item.Id, RecordCode = item.Code, After = Snapshot(item) });
        if (await SaveAsync(values, null, cancellationToken) is { } error)
        {
            return error;
        }

        return View(item, isUsed: false);
    }

    /// <summary>PURCHASE-08.1: an In Use item only; group and unit stay as they are once the item is used.</summary>
    public async Task<Result<FarmingInputItemView>> UpdateAsync(Guid itemId, SaveFarmingInputItemRequest request, CancellationToken cancellationToken)
    {
        var item = await items.GetForEditAsync(itemId, cancellationToken);
        if (item is null)
        {
            return AppError.NotFound;
        }

        if (item.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {item.Code}", statusCatalog.DisplayNameOf(item.StatusId)));
        }

        var (values, invalid) = Validate(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var used = await items.IsUsedAsync(item.Id, cancellationToken);
        if (used && FixedOnceUsed(item, values) is { } field)
        {
            return AppError.Invalid(field, "create a new item instead");
        }

        // Checked only when the group or the unit changes, so an item saved before the rule can still be renamed.
        var seedUnitChanged = item.Group != values.Group || item.Unit != values.Unit;
        if (seedUnitChanged && await CheckSeedUnitAsync(values, item.Id, cancellationToken) is { } seedUnit)
        {
            return seedUnit;
        }

        if (await DuplicateAsync(values, item.Id, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        if (await rowVersions.CheckAsync(item, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(item);
        Apply(item, values);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(item));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("FarmingInputItem.Updated", RecordType) { RecordId = item.Id, RecordCode = item.Code, Before = changedBefore, After = changedAfter });
            if (await SaveAsync(values, item.Id, cancellationToken) is { } error)
            {
                return error;
            }
        }

        return View(item, used);
    }

    public Task<Result<FarmingInputItemView>> DiscontinueAsync(Guid itemId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(itemId, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, "FarmingInputItem.Discontinued", cancellationToken);

    /// <summary>Resume (PURCHASE-08.2): back to In Use with the same code and history.</summary>
    public Task<Result<FarmingInputItemView>> ReactivateAsync(Guid itemId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(itemId, StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, "FarmingInputItem.Reactivated", cancellationToken);

    private async Task<Result<FarmingInputItemView>> ChangeStatusAsync(
        Guid itemId, int fromStatusId, int toStatusId, string action, CancellationToken cancellationToken)
    {
        var item = await items.GetForEditAsync(itemId, cancellationToken);
        if (item is null)
        {
            return AppError.NotFound;
        }

        if (item.StatusId != fromStatusId)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {item.Code}", statusCatalog.DisplayNameOf(item.StatusId)));
        }

        item.StatusId = toStatusId;
        audit.Record(new AuditEntry(action, RecordType)
        {
            RecordId = item.Id, RecordCode = item.Code,
            Before = new { Status = statusCatalog.CodeOf(fromStatusId) }, After = new { Status = statusCatalog.CodeOf(toStatusId) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(item, await items.IsUsedAsync(item.Id, cancellationToken));
    }

    private async Task<bool> MayReadAsync(CancellationToken cancellationToken)
    {
        foreach (var function in new[] { PermissionFunctions.PurchaseReceipts, PermissionFunctions.SupplierPayments, PermissionFunctions.CultivationCycles })
        {
            if (!(await zoneAccess.PermittedZonesAsync(function, PermissionAction.View, cancellationToken)).IsEmpty)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// BR-PURCHASE-10: a Seed item takes the unit of the other Seed items (discontinued ones included); the first Seed
    /// item chooses it. MSG-GEN-01 on the unit, with the unit to use in <c>seedUnit</c>.
    /// </summary>
    private async Task<AppError?> CheckSeedUnitAsync(Values values, Guid? itemId, CancellationToken cancellationToken)
    {
        if (values.Group != InputItemGroup.Seed || await items.SeedUnitAsync(itemId, cancellationToken) is not { } seedUnit || seedUnit == values.Unit)
        {
            return null;
        }

        return new AppError(
            ErrorKind.Validation,
            UserMessages.Invalid("Unit"),
            new Dictionary<string, object?> { ["field"] = "Unit", ["hint"] = $"all seed items use the unit {seedUnit}", ["seedUnit"] = seedUnit });
    }

    /// <summary>MSG-GEN-02 with the id of the other item that has the same code or name (GBR-03, BR-PURCHASE-10).</summary>
    private async Task<AppError?> DuplicateAsync(Values values, Guid? itemId, CancellationToken cancellationToken) =>
        (await items.FindByCodeAsync(values.Code, itemId, cancellationToken) ?? await items.FindByNameAsync(values.Name, itemId, cancellationToken)) is { } existing
            ? AppError.AlreadyExists(ObjectName, existing.Id)
            : null;

    /// <summary>The second of two parallel saves with one code or name gets the same MSG-GEN-02 as the pre-check.</summary>
    private async Task<AppError?> SaveAsync(Values values, Guid? itemId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            if (await DuplicateAsync(values, itemId, cancellationToken) is { } duplicate)
            {
                return duplicate;
            }

            throw;
        }
    }

    /// <summary>The first of group and unit that a used item may not change (the code may change, GBR-03).</summary>
    private static string? FixedOnceUsed(FarmingInputItem item, Values values) =>
        item.Group != values.Group ? "Group"
        : item.Unit != values.Unit ? "Unit"
        : null;

    private static (Values Values, List<string> Invalid) Validate(SaveFarmingInputItemRequest request)
    {
        var check = new List<string>();
        string Text(string? value, string field, int max, bool required)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if ((required && trimmed.Length == 0) || trimmed.Length > max)
            {
                check.Add(field);
            }

            return trimmed;
        }

        var code = Text(request.Code, "Code", 50, true);
        var name = Text(request.Name, "Name", 200, true);
        if (request.Group is not { } group || !Enum.IsDefined(group))
        {
            check.Add("Group");
        }

        var unit = Text(request.Unit, "Unit", 30, true);
        var description = Text(request.Description, "Description", 2000, false);
        return (new Values(code, name, request.Group ?? default, unit, description.Length == 0 ? null : description), check);
    }

    private static void Apply(FarmingInputItem item, Values values)
    {
        item.Code = values.Code;
        item.Name = values.Name;
        item.Group = values.Group;
        item.Unit = values.Unit;
        item.Description = values.Description;
    }

    private static object Snapshot(FarmingInputItem i) => new { i.Code, i.Name, i.Group, i.Unit, i.Description };

    private FarmingInputItemView View(FarmingInputItem i, bool isUsed) => new(
        i.Id, i.Code, i.Name, i.Group, i.Unit, i.Description, isUsed, i.StatusId, statusCatalog.DisplayNameOf(i.StatusId), RowVersionGuard.Encode(i.RowVersion));

    private sealed record Values(string Code, string Name, InputItemGroup Group, string Unit, string? Description);
}
