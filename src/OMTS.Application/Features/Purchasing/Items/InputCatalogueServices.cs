using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>
/// The unit catalogue of the cooperative ("Đơn vị tính" of the settings): reading follows the item catalogue, writes are Owner only (API filter). A name is unique regardless of letter case;
/// a unit that some item uses is not deleted, and renaming one renames the base unit shown on the items that count in it (receipt lines keep the text they were saved with).
/// </summary>
public sealed class InputUnitService(
    IInputUnitRepository units, InputItemAccess access, RowVersionGuard rowVersions, AuditLogService audit, IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(InputUnit);
    private const string ObjectName = "Unit";

    public async Task<Result<IReadOnlyList<InputUnitView>>> ListAsync(CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var usage = await units.UsageAsync(cancellationToken);
        return Result<IReadOnlyList<InputUnitView>>.Success(
            (await units.ListAsync(cancellationToken)).Select(u => View(u, usage.GetValueOrDefault(u.Id))).ToList());
    }

    public async Task<Result<InputUnitView>> CreateAsync(SaveInputUnitRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > InputItemUnitRules.MaxNameLength)
        {
            return AppError.InvalidFields(["Name"]);
        }

        if (await units.FindByNameAsync(name, null, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        var unit = new InputUnit { Name = name };
        units.Add(unit);
        audit.Record(new AuditEntry("InputUnit.Created", RecordType) { RecordId = unit.Id, RecordCode = unit.Name, After = new { unit.Name } });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            return await units.FindByNameAsync(name, null, cancellationToken) is { } other
                ? AppError.AlreadyExists(ObjectName, other.Id, "Name")
                : AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }

        return View(unit, null);
    }

    public async Task<Result<InputUnitView>> UpdateAsync(Guid unitId, SaveInputUnitRequest request, CancellationToken cancellationToken)
    {
        var unit = await units.GetForEditAsync(unitId, cancellationToken);
        if (unit is null)
        {
            return AppError.NotFound;
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > InputItemUnitRules.MaxNameLength)
        {
            return AppError.InvalidFields(["Name"]);
        }

        if (await rowVersions.CheckAsync(unit, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (await units.FindByNameAsync(name, unit.Id, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        if (name != unit.Name)
        {
            audit.Record(new AuditEntry("InputUnit.Updated", RecordType)
            {
                RecordId = unit.Id, RecordCode = name, Before = new { Name = unit.Name }, After = new { Name = name },
            });
            unit.Name = name;
            foreach (var item in await units.ItemsWithBaseUnitAsync(unit.Id, cancellationToken))
            {
                item.Unit = name;
            }
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            return AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }

        return View(unit, (await units.UsageAsync(cancellationToken)).GetValueOrDefault(unit.Id));
    }

    /// <summary>Deletes a unit no item uses; MSG-GEN-06 with the number of item units otherwise.</summary>
    public async Task<Result<Done>> DeleteAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var unit = await units.GetForEditAsync(unitId, cancellationToken);
        if (unit is null)
        {
            return AppError.NotFound;
        }

        if ((await units.UsageAsync(cancellationToken)).GetValueOrDefault(unit.Id) is { ItemUnits: > 0 } usage)
        {
            return StillUsed(usage.ItemUnits);
        }

        units.Remove(unit);
        audit.Record(new AuditEntry("InputUnit.Deleted", RecordType) { RecordId = unit.Id, RecordCode = unit.Name, Before = new { unit.Name } });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ForeignKeyViolationException)
        {
            // An item took the unit after the check.
            unitOfWork.DiscardChanges();
            return StillUsed((await units.UsageAsync(cancellationToken)).GetValueOrDefault(unitId)?.ItemUnits ?? 1);
        }

        return Done.Value;
    }

    private static AppError StillUsed(int itemUnits) => AppError.Conflict(
        UserMessages.StillInUse(1, $"items ({itemUnits})"),
        new Dictionary<string, object?> { ["references"] = new[] { new { Name = "Items", Count = itemUnits } } });

    private static InputUnitView View(InputUnit unit, InputUnitUsage? usage) =>
        new(unit.Id, unit.Name, usage?.ItemUnits ?? 0, usage?.OnLines ?? false, RowVersionGuard.Encode(unit.RowVersion));
}

/// <summary>
/// The user groups of the items ("Nhóm hàng"): a name unique regardless of letter case, an order the Owner sets, and the number of items in each. Reading follows the item catalogue, writes
/// are Owner only (API filter). A group is deleted only when no item belongs to it; deleted items let go of it.
/// </summary>
public sealed class InputItemGroupService(
    IInputItemCategoryRepository groups, InputItemAccess access, RowVersionGuard rowVersions, AuditLogService audit, IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(InputItemCategory);
    private const string ObjectName = "Item group";
    public const int MaxNameLength = 100;
    public const int MaxGroups = 200;

    public async Task<Result<IReadOnlyList<InputItemGroupView>>> ListAsync(CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        return Result<IReadOnlyList<InputItemGroupView>>.Success(await ViewsAsync(cancellationToken));
    }

    public async Task<Result<InputItemGroupView>> CreateAsync(SaveInputItemGroupRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return AppError.InvalidFields(["Name"]);
        }

        var all = await groups.ListAsync(cancellationToken);
        if (all.Count >= MaxGroups)
        {
            return AppError.Invalid("Name", $"At most {MaxGroups} groups.");
        }

        if (ParentError(request.ParentId, null, all) is { } parentError)
        {
            return parentError;
        }

        if (await groups.FindByNameAsync(name, null, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        var group = new InputItemCategory { Name = name, ParentId = request.ParentId, SortOrder = all.Select(g => g.SortOrder).DefaultIfEmpty(-1).Max() + 1 };
        groups.Add(group);
        audit.Record(new AuditEntry("InputItemGroup.Created", RecordType) { RecordId = group.Id, RecordCode = group.Name, After = new { group.Name, group.SortOrder, group.ParentId } });
        if (await SaveAsync(name, cancellationToken) is { } error)
        {
            return error;
        }

        return new InputItemGroupView(group.Id, group.Name, group.SortOrder, 0, RowVersionGuard.Encode(group.RowVersion), group.ParentId);
    }

    /// <summary>
    /// MSG-GEN-01 (field Parent) when the parent is not a top-level group, is the group itself, or the group already has children: a group tree is two levels deep at most and
    /// never loops. <paramref name="selfId"/> is the group being edited, null for a new one.
    /// </summary>
    private static AppError? ParentError(Guid? parentId, Guid? selfId, IReadOnlyList<InputItemCategory> all)
    {
        if (parentId is not { } parent)
        {
            return null;
        }

        var found = all.FirstOrDefault(g => g.Id == parent);
        return found is null || found.ParentId is not null || parent == selfId || (selfId is { } self && all.Any(g => g.ParentId == self))
            ? AppError.InvalidFields(["Parent"])
            : null;
    }

    public async Task<Result<InputItemGroupView>> UpdateAsync(Guid groupId, SaveInputItemGroupRequest request, CancellationToken cancellationToken)
    {
        var group = await groups.GetForEditAsync(groupId, cancellationToken);
        if (group is null)
        {
            return AppError.NotFound;
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return AppError.InvalidFields(["Name"]);
        }

        if (await rowVersions.CheckAsync(group, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (await groups.FindByNameAsync(name, group.Id, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        if (ParentError(request.ParentId, group.Id, await groups.ListAsync(cancellationToken)) is { } parentError)
        {
            return parentError;
        }

        if (name != group.Name || request.ParentId != group.ParentId)
        {
            audit.Record(new AuditEntry("InputItemGroup.Updated", RecordType)
            {
                RecordId = group.Id, RecordCode = name, Before = new { group.Name, group.ParentId }, After = new { Name = name, request.ParentId },
            });
            group.Name = name;
            group.ParentId = request.ParentId;
            if (await SaveAsync(name, cancellationToken) is { } error)
            {
                return error;
            }
        }

        return (await ViewsAsync(cancellationToken)).First(v => v.Id == group.Id);
    }

    /// <summary>Puts the groups in the order of <c>ids</c>, which must name every group once.</summary>
    public async Task<Result<IReadOnlyList<InputItemGroupView>>> ReorderAsync(ReorderInputItemGroupsRequest request, CancellationToken cancellationToken)
    {
        var all = await groups.GetAllForEditAsync(cancellationToken);
        var ids = request.Ids ?? [];
        if (ids.Count != all.Count || ids.Distinct().Count() != ids.Count || !all.Select(g => g.Id).ToHashSet().SetEquals(ids))
        {
            return AppError.InvalidFields(["Ids"]);
        }

        var byId = all.ToDictionary(g => g.Id);
        var changed = false;
        for (var order = 0; order < ids.Count; order++)
        {
            var group = byId[ids[order]];
            if (group.SortOrder != order)
            {
                group.SortOrder = order;
                changed = true;
            }
        }

        if (changed)
        {
            audit.Record(new AuditEntry("InputItemGroup.Reordered", RecordType) { After = new { Order = ids.Select(id => byId[id].Name).ToList() } });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<IReadOnlyList<InputItemGroupView>>.Success(await ViewsAsync(cancellationToken));
    }

    /// <summary>Deletes a group no item (that is not deleted) belongs to; MSG-GEN-06 with the number of items otherwise.</summary>
    public async Task<Result<Done>> DeleteAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var group = await groups.GetForEditAsync(groupId, cancellationToken);
        if (group is null)
        {
            return AppError.NotFound;
        }

        if ((await groups.ListAsync(cancellationToken)).Count(g => g.ParentId == group.Id) is var children and > 0)
        {
            return StillUsed(children, "child groups");
        }

        if ((await groups.ItemCountsAsync(cancellationToken)).GetValueOrDefault(group.Id) is var count and > 0)
        {
            return StillUsed(count, "items");
        }

        foreach (var hidden in await groups.HiddenItemsAsync(group.Id, cancellationToken))
        {
            hidden.ItemGroupId = null;
        }

        groups.Remove(group);
        audit.Record(new AuditEntry("InputItemGroup.Deleted", RecordType) { RecordId = group.Id, RecordCode = group.Name, Before = new { group.Name } });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ForeignKeyViolationException)
        {
            // An item joined the group after the check.
            unitOfWork.DiscardChanges();
            return StillUsed((await groups.ItemCountsAsync(cancellationToken)).GetValueOrDefault(groupId, 1), "items");
        }

        return Done.Value;
    }

    private static AppError StillUsed(int count, string what) => AppError.Conflict(
        UserMessages.StillInUse(1, $"{what} ({count})"),
        new Dictionary<string, object?> { ["references"] = new[] { new { Name = what == "items" ? "Items" : "Child groups", Count = count } } });

    private async Task<AppError?> SaveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            return await groups.FindByNameAsync(name, null, cancellationToken) is { } other
                ? AppError.AlreadyExists(ObjectName, other.Id, "Name")
                : AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }
    }

    private async Task<IReadOnlyList<InputItemGroupView>> ViewsAsync(CancellationToken cancellationToken)
    {
        var counts = await groups.ItemCountsAsync(cancellationToken);
        var all = await groups.ListAsync(cancellationToken);
        // A parent counts its own items and those of its children.
        return all.Select(g => new InputItemGroupView(
            g.Id, g.Name, g.SortOrder, counts.GetValueOrDefault(g.Id) + all.Where(c => c.ParentId == g.Id).Sum(c => counts.GetValueOrDefault(c.Id)), RowVersionGuard.Encode(g.RowVersion), g.ParentId)).ToList();
    }
}

/// <summary>
/// The inventory settings of the cooperative: how the cost of input items is kept and whether a document may take an item below zero. Read by whoever reads the item catalogue, saved by the
/// Owner only (API filter). They live on the cooperative row, like the harvest settings, so saving them also makes an open profile form stale, and the other way round.
/// </summary>
public sealed class InputInventorySettingsService(
    ICooperativeRepository cooperatives, InputItemAccess access, RowVersionGuard rowVersions, AuditLogService audit, IUnitOfWork unitOfWork)
{
    public async Task<Result<InputInventorySettingsView>> GetAsync(CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        return await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative ? View(cooperative) : AppError.NotFound;
    }

    public async Task<Result<InputInventorySettingsView>> UpdateAsync(UpdateInputInventorySettingsRequest request, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (cooperative is null)
        {
            return AppError.NotFound;
        }

        var invalid = new List<string>();
        if (request.CostMethod is not { } method || !Enum.IsDefined(method))
        {
            invalid.Add("Cost method");
        }

        if (request.AllowNegativeQuantity is null)
        {
            invalid.Add("Allow negative quantity");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await rowVersions.CheckAsync(cooperative, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = new { cooperative.InputCostMethod, cooperative.AllowNegativeInputQuantity };
        cooperative.InputCostMethod = request.CostMethod!.Value;
        cooperative.AllowNegativeInputQuantity = request.AllowNegativeQuantity!.Value;
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, new { cooperative.InputCostMethod, cooperative.AllowNegativeInputQuantity });
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Cooperative.InputInventorySettingsChanged", nameof(Domain.Entities.Identity.Cooperative))
            {
                RecordId = cooperative.Id, Before = changedBefore, After = changedAfter,
            });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(cooperative);
    }

    private static InputInventorySettingsView View(Domain.Entities.Identity.Cooperative cooperative) =>
        new(cooperative.InputCostMethod, cooperative.AllowNegativeInputQuantity, RowVersionGuard.Encode(cooperative.RowVersion));
}
