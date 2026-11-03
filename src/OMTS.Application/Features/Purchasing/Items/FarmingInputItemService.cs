using OMTS.Application.Common.Concurrency;
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

public sealed record SaveFarmingInputItemRequest(
    string? Code, string? Name, InputItemGroup? Group, string? Unit, string? Description, string? RowVersion = null);

public sealed record FarmingInputItemView(
    Guid Id, string Code, string Name, InputItemGroup Group, string Unit, string? Description, bool IsUsed, int StatusId, string Status, string RowVersion);

/// <summary>
/// INP-02 – the Cooperative Owner's catalogue of farming inputs (writes are Owner only, API filter). Group
/// Seed items are the oyster seed types. Code, group and unit cannot change once the item is used.
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

    public async Task<Result<FarmingInputItemView>> CreateAsync(SaveFarmingInputItemRequest request, CancellationToken cancellationToken)
    {
        var (values, invalid) = Validate(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await items.FindByCodeAsync(values.Code, null, cancellationToken) is { } existing)
        {
            return AppError.AlreadyExists("Farming input item", existing.Id);
        }

        var item = new FarmingInputItem();
        Apply(item, values);
        items.Add(item);
        audit.Record(new AuditEntry("FarmingInputItem.Created", RecordType) { RecordId = item.Id, RecordCode = item.Code, After = Snapshot(item) });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(item, isUsed: false);
    }

    public async Task<Result<FarmingInputItemView>> UpdateAsync(Guid itemId, SaveFarmingInputItemRequest request, CancellationToken cancellationToken)
    {
        var item = await items.GetForEditAsync(itemId, cancellationToken);
        if (item is null)
        {
            return AppError.NotFound;
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

        if (await items.FindByCodeAsync(values.Code, item.Id, cancellationToken) is { } existing)
        {
            return AppError.AlreadyExists("Farming input item", existing.Id);
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
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return View(item, used);
    }

    public Task<Result<FarmingInputItemView>> DiscontinueAsync(Guid itemId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(itemId, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, "FarmingInputItem.Discontinued", cancellationToken);

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
            return AppError.Conflict(UserMessages.WrongStatus($"Farming input item {item.Code}", statusCatalog.DisplayNameOf(item.StatusId)));
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

    /// <summary>The first of code, group and unit that a used item may not change.</summary>
    private static string? FixedOnceUsed(FarmingInputItem item, Values values) =>
        !string.Equals(item.Code, values.Code, StringComparison.OrdinalIgnoreCase) ? "Code"
        : item.Group != values.Group ? "Group"
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
