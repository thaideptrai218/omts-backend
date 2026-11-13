using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>
/// The Cooperative Owner's catalogue of farming input items (writes are Owner only, API filter). Code (typed, or GI/VT/HK000001 by type) and name are unique, discontinued items included
/// (GBR-03, BR-PURCHASE-10); the type is fixed once the item is used; every item has one base unit (quantity, cost and price are per base unit) and any number of other units with a
/// factor; the base unit is fixed once the item has movements; only an In Use item is edited; a Discontinued item can be resumed (GBR-01).
/// </summary>
public sealed class FarmingInputItemService(
    IFarmingInputItemRepository items,
    IInputStockRepository stock,
    IInputUnitRepository units,
    IInputItemCategoryRepository groups,
    IProductRepository products,
    IInputItemListQuery list,
    FarmingInputItemCodeService codes,
    InputItemAccess access,
    InputStockLedger ledger,
    IAccountRepository accounts,
    IFileStorage storage,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public const int MaxPhotos = 5;
    internal const string RecordType = nameof(FarmingInputItem);
    internal const string ObjectName = "Farming input item";
    private const int MaxPageSize = 100;
    private const decimal MaxQuantity = 1_000_000_000m;
    private const decimal MaxCost = 1_000_000_000_000m;
    private const string PhotoCategory = "input-item-photos";

    /// <summary>Readable by whoever views receipts, supplier payments or cultivation cycles (pickers, filters). The items that are not deleted, with their units; no photos.</summary>
    public async Task<Result<IReadOnlyList<FarmingInputItemListItem>>> ListAsync(
        string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var found = await items.ListAsync(search, group, statusId, cancellationToken);
        var onHand = await stock.OnHandAsync(found.Select(i => i.Id).ToList(), cancellationToken);
        return Result<IReadOnlyList<FarmingInputItemListItem>>.Success(
            await ListItemsAsync(found.Select(i => new InputItemRow(i, onHand.GetValueOrDefault(i.Id))).ToList(), withPhotos: false, cancellationToken));
    }

    /// <summary>The paged list with filters, sort and the stock value of every match.</summary>
    public async Task<Result<FarmingInputItemPage>> SearchAsync(FarmingInputItemListQuery query, CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var filter = FilterOf(query);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // The upper bound keeps (page - 1) * pageSize inside int.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var (rows, total, stockValue) = await list.SearchAsync(filter.Value!, page, pageSize, cancellationToken);
        return new FarmingInputItemPage(await ListItemsAsync(rows, withPhotos: true, cancellationToken), page, pageSize, total, new FarmingInputItemListSummary(stockValue));
    }

    /// <summary>Every matching item for the export, with the same filters as the list.</summary>
    internal async Task<Result<IReadOnlyList<FarmingInputItemListItem>>> ExportRowsAsync(FarmingInputItemListQuery query, CancellationToken cancellationToken)
    {
        var filter = FilterOf(query);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        return Result<IReadOnlyList<FarmingInputItemListItem>>.Success(
            await ListItemsAsync(await list.ListAsync(filter.Value!, cancellationToken), withPhotos: false, cancellationToken));
    }

    public async Task<Result<FarmingInputItemView>> GetAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        return await items.GetForEditAsync(itemId, cancellationToken) is { IsHidden: false } item ? await ViewAsync(item, cancellationToken) : AppError.NotFound;
    }

    /// <summary>The code a new item of the type would get (GI000001, VT000001, HK000001), which the Owner may replace.</summary>
    public async Task<NewFarmingInputItemProposal> ProposeAsync(InputItemGroup group, CancellationToken cancellationToken) =>
        new(await codes.PeekAsync(group, cancellationToken));

    public async Task<Result<FarmingInputItemView>> CreateAsync(SaveFarmingInputItemRequest request, CancellationToken cancellationToken)
    {
        var (values, invalid) = await ValidateAsync(request, cancellationToken);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await DuplicateNameAsync(values.Name, null, cancellationToken) is { } duplicateName)
        {
            return duplicateName;
        }

        var (code, codeError) = await codes.ForNewAsync(request.Code, values.Group, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var item = new FarmingInputItem { Code = code! };
        Apply(item, values);
        var planned = await PlanUnitsAsync(request, item, existing: null, cancellationToken);
        if (planned.Error is not null)
        {
            return planned.Error;
        }

        items.Add(item);
        foreach (var plan in planned.Plans!)
        {
            items.AddUnit(NewUnit(item, plan));
        }

        item.Unit = planned.Plans!.Single(p => p.IsBase).Catalogue.Name;
        if (values.OpeningQuantity is { } opening)
        {
            await ledger.PostOpeningAsync(item, opening, cancellationToken);
        }

        audit.Record(new AuditEntry("FarmingInputItem.Created", RecordType) { RecordId = item.Id, RecordCode = item.Code, After = Snapshot(item, planned.Plans!) });
        if (await SaveAsync(values.Name, item.Id, cancellationToken) is { } error)
        {
            return error;
        }

        return await ViewAsync(item, cancellationToken);
    }

    /// <summary>An In Use item only; the type stays once the item is used, and so does the base unit once it has movements.</summary>
    public async Task<Result<FarmingInputItemView>> UpdateAsync(Guid itemId, SaveFarmingInputItemRequest request, CancellationToken cancellationToken)
    {
        var item = await items.GetForEditAsync(itemId, cancellationToken);
        if (item is not { IsHidden: false })
        {
            return AppError.NotFound;
        }

        if (item.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {item.Code}", statusCatalog.DisplayNameOf(item.StatusId)));
        }

        var (values, invalid) = await ValidateAsync(request, cancellationToken);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (item.Group != values.Group && await items.IsUsedAsync(item.Id, cancellationToken))
        {
            return AppError.Invalid("Group", "create a new item instead");
        }

        if (await DuplicateNameAsync(values.Name, item.Id, cancellationToken) is { } duplicateName)
        {
            return duplicateName;
        }

        var (code, codeError) = await codes.ForEditAsync(request.Code, item.Id, item.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        if (await rowVersions.CheckAsync(item, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(item, null);
        item.Code = code!;
        Apply(item, values);
        var planned = await PlanUnitsAsync(request, item, existing: item.Units.ToList(), cancellationToken);
        if (planned.Error is not null)
        {
            return planned.Error;
        }

        if (planned.Plans is { } plans)
        {
            ApplyUnits(item, plans);
        }

        if (values.OpeningQuantity is { } opening && await OpeningErrorAsync(item, opening, cancellationToken) is { } openingError)
        {
            return openingError;
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(item, null));
        if (changedAfter is not null)
        {
            // Units, photos and movements are rows of their own: the item still gets a new row version.
            unitOfWork.MarkChanged(item);
            audit.Record(new AuditEntry("FarmingInputItem.Updated", RecordType) { RecordId = item.Id, RecordCode = item.Code, Before = changedBefore, After = changedAfter });
        }

        if (await SaveAsync(values.Name, item.Id, cancellationToken) is { } error)
        {
            return error;
        }

        return await ViewAsync(item, cancellationToken);
    }

    public Task<Result<FarmingInputItemView>> DiscontinueAsync(Guid itemId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(itemId, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, "FarmingInputItem.Discontinued", cancellationToken);

    /// <summary>Resume (PURCHASE-08.2): back to In Use with the same code and history.</summary>
    public Task<Result<FarmingInputItemView>> ReactivateAsync(Guid itemId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(itemId, StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, "FarmingInputItem.Reactivated", cancellationToken);

    /// <summary>JPEG or PNG of at most 2 MB, added to the photos (at most five); a failed upload leaves the item unchanged (MSG-GEN-03).</summary>
    public async Task<Result<FarmingInputItemView>> AddPhotoAsync(Guid itemId, IncomingFile? file, CancellationToken cancellationToken)
    {
        var item = await items.GetForEditAsync(itemId, cancellationToken);
        if (item is not { IsHidden: false })
        {
            return AppError.NotFound;
        }

        var validated = FileValidator.Validate(file is null ? [] : [file], FileRules.ItemPhoto);
        if (file is null || !validated.IsValid)
        {
            return AppError.Invalid(validated.InvalidFileName ?? "Photo", validated.Problem);
        }

        if (item.Photos.Count >= MaxPhotos)
        {
            return AppError.Invalid("Photos", $"At most {MaxPhotos} photos.");
        }

        var uploaded = await storage.UploadAsync(validated.Files[0], PhotoCategory, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.ServiceUnavailable, UserMessages.UploadFailed with { LegacyCode = MessageCodes.Legacy.Msg04 });
        }

        var photo = new InputItemPhoto { ItemId = item.Id, StorageKey = uploaded.Key!, SortOrder = item.Photos.Select(p => p.SortOrder).DefaultIfEmpty(-1).Max() + 1 };
        items.AddPhoto(photo);
        item.Photos.Add(photo);
        audit.Record(new AuditEntry("FarmingInputItem.Updated", RecordType)
        {
            RecordId = item.Id, RecordCode = item.Code, Before = new { Photos = item.Photos.Count - 1 }, After = new { Photos = item.Photos.Count },
        });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await storage.DeleteAsync(uploaded.Key!, cancellationToken);
            throw;
        }

        return await ViewAsync(item, cancellationToken);
    }

    public async Task<Result<FarmingInputItemView>> RemovePhotoAsync(Guid itemId, Guid photoId, CancellationToken cancellationToken)
    {
        var item = await items.GetForEditAsync(itemId, cancellationToken);
        if (item is not { IsHidden: false } || item.Photos.FirstOrDefault(p => p.Id == photoId) is not { } photo)
        {
            return AppError.NotFound;
        }

        item.Photos.Remove(photo);
        items.RemovePhoto(photo);
        audit.Record(new AuditEntry("FarmingInputItem.Updated", RecordType)
        {
            RecordId = item.Id, RecordCode = item.Code, Before = new { Photos = item.Photos.Count + 1 }, After = new { Photos = item.Photos.Count },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(photo.StorageKey, cancellationToken);
        return await ViewAsync(item, cancellationToken);
    }

    private async Task<Result<FarmingInputItemView>> ChangeStatusAsync(Guid itemId, int fromStatusId, int toStatusId, string action, CancellationToken cancellationToken)
    {
        var item = await items.GetForEditAsync(itemId, cancellationToken);
        if (item is not { IsHidden: false })
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
        return await ViewAsync(item, cancellationToken);
    }

    /// <summary>The opening quantity of an item can be corrected while it is the item's only movement; a request that does not send one leaves it.</summary>
    private async Task<AppError?> OpeningErrorAsync(FarmingInputItem item, decimal opening, CancellationToken cancellationToken)
    {
        var current = await stock.OpeningQuantityAsync(item.Id, cancellationToken);
        if ((current ?? 0m) == opening)
        {
            return null;
        }

        if (!await stock.HasOnlyOpeningAsync(item.Id, cancellationToken))
        {
            return AppError.Invalid("Opening quantity", "The item has documents: its quantity changes through them.");
        }

        await ledger.ReverseAsync(item.Id, afterReceiptRemoved: false, cancellationToken);
        await ledger.PostOpeningAsync(item, opening, cancellationToken);
        return null;
    }

    /// <summary>MSG-GEN-02 with the id of the other item that has the same name (BR-PURCHASE-10); the code is checked by <see cref="FarmingInputItemCodeService"/>.</summary>
    private async Task<AppError?> DuplicateNameAsync(string name, Guid? itemId, CancellationToken cancellationToken) =>
        await items.FindByNameAsync(name, itemId, cancellationToken) is { } sameName ? AppError.AlreadyExists(ObjectName, sameName.Id, "Name") : null;

    /// <summary>The second of two parallel saves with one code, name or unit code gets MSG-GEN-02, like the pre-check.</summary>
    private async Task<AppError?> SaveAsync(string name, Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            return await DuplicateNameAsync(name, itemId, cancellationToken) ?? AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }
    }

    private sealed record Values(
        string Name, InputItemGroup Group, string? Description, string? Notes, Guid? ItemGroupId, decimal? MinQuantity, decimal? MaxQuantity, decimal? CostPrice, decimal? OpeningQuantity, Product? Product);

    private async Task<(Values Values, List<string> Invalid)> ValidateAsync(SaveFarmingInputItemRequest request, CancellationToken cancellationToken)
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

        bool Amount(decimal? value, string field, decimal max, int decimals)
        {
            if (value is { } v && (v < 0 || v > max || decimal.Round(v, decimals) != v))
            {
                check.Add(field);
                return false;
            }

            return true;
        }

        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            check.Add("Code");
        }

        var name = Text(request.Name, "Name", 200, true);
        if (request.Group is not { } group || !Enum.IsDefined(group))
        {
            check.Add("Group");
        }

        var description = Text(request.Description, "Description", 2000, false);
        var notes = Text(request.Notes, "Notes", 2000, false);
        if (request.ItemGroupId is { } itemGroupId && await groups.GetForEditAsync(itemGroupId, cancellationToken) is null)
        {
            check.Add("Item group");
        }

        // The product sold when this seed is grown (13/11/2026): Seed items only, the other types ignore it; it must be in the catalogue and still sold.
        Product? product = null;
        if (request.Group == InputItemGroup.Seed && request.ProductId is { } productId)
        {
            product = await products.GetForEditAsync(productId, cancellationToken);
            if (product is null || product.StatusId != StatusIds.Usage.InUse)
            {
                check.Add("ProductId");
                product = null;
            }
        }

        var minOk = Amount(request.MinQuantity, "Min quantity", MaxQuantity, 3);
        var maxOk = Amount(request.MaxQuantity, "Max quantity", MaxQuantity, 3);
        if (minOk && maxOk && request.MinQuantity > request.MaxQuantity)
        {
            check.Add("Max quantity");
        }

        Amount(request.CostPrice, "Cost price", MaxCost, 4);
        Amount(request.OpeningQuantity, "Opening quantity", MaxQuantity, 2);
        return (new Values(
            name, request.Group ?? default, description.Length == 0 ? null : description, notes.Length == 0 ? null : notes, request.ItemGroupId,
            request.MinQuantity, request.MaxQuantity, request.CostPrice, request.OpeningQuantity, product), check);
    }

    private static void Apply(FarmingInputItem item, Values values)
    {
        item.Name = values.Name;
        item.Group = values.Group;
        item.Description = values.Description;
        item.Notes = values.Notes;
        item.ItemGroupId = values.ItemGroupId;
        item.MinQuantity = values.MinQuantity;
        item.MaxQuantity = values.MaxQuantity;
        item.CostPrice = values.CostPrice;
        item.ProductId = values.Product?.Id;
        item.Product = values.Product;
    }

    /// <summary>One unit of the item as it will be saved: an existing row (<see cref="Row"/>) or a new one, with its catalogue unit, factor and code.</summary>
    private sealed record UnitPlan(InputItemUnit? Row, InputUnit Catalogue, decimal Factor, string Code, bool IsBase);

    /// <summary>The units a save leaves the item with (null when an update keeps them), or the first error.</summary>
    private async Task<(IReadOnlyList<UnitPlan>? Plans, AppError? Error)> PlanUnitsAsync(
        SaveFarmingInputItemRequest request, FarmingInputItem item, IReadOnlyList<InputItemUnit>? existing, CancellationToken cancellationToken)
    {
        var inputs = request.Units;
        var hasMovements = existing is not null && await items.HasMovementsAsync(item.Id, cancellationToken);
        if (inputs is null)
        {
            var name = request.Unit?.Trim();
            if (existing is null)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return (null, AppError.InvalidFields(["Unit"]));
                }

                inputs = [new ItemUnitInput(null, null, name, 1m, null, true)];
            }
            else
            {
                // The shortcut of old callers: "unit" alone renames the base unit of the item, the other units stay.
                var current = existing.FirstOrDefault(u => u.IsBase);
                if (string.IsNullOrEmpty(name) || current is null || string.Equals(name, current.Unit?.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null);
                }

                if (hasMovements)
                {
                    return (null, AppError.Invalid("Unit", "create a new item instead"));
                }

                inputs = existing.Select(u => u.IsBase ? new ItemUnitInput(u.Id, null, name, 1m, u.Code, true) : new ItemUnitInput(u.Id, u.UnitId, null, u.Factor, u.Code, false)).ToList();
            }
        }

        if (inputs.Count is 0 or > 20 || inputs.Any(i => i is null) || inputs.Count(i => i.IsBase) != 1)
        {
            return (null, AppError.InvalidFields(["Units"]));
        }

        var usedUnits = existing is null ? new HashSet<Guid>() : await items.UsedUnitsAmongAsync([item.Id], cancellationToken);
        var baseRow = existing?.FirstOrDefault(u => u.IsBase);
        var plans = new List<UnitPlan>();
        var takenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { item.Code };
        var seenRows = new HashSet<Guid>();
        foreach (var input in inputs)
        {
            // The base row of the request is the base row of the item (its unit may change while the item has no movement); an extra row names its own id or is new.
            var row = input.IsBase ? baseRow : existing?.FirstOrDefault(u => u.Id == input.Id);
            if ((input.Id is not null && row is null) || (row is not null && !seenRows.Add(row.Id)) || (input.IsBase && input.Id is not null && input.Id != baseRow?.Id)
                || (!input.IsBase && row is not null && row == baseRow))
            {
                return (null, AppError.Invalid("Units", "A unit is named twice or is not one of the item's units; the base unit cannot be exchanged."));
            }

            var catalogue = await CatalogueUnitAsync(input, cancellationToken);
            if (catalogue is null)
            {
                return (null, AppError.InvalidFields(["Units"]));
            }

            var factor = input.IsBase ? 1m : input.Factor;
            if (!input.IsBase && !InputItemUnitRules.FactorAllowed(factor))
            {
                return (null, AppError.InvalidFields(["Factor"]));
            }

            if (row is not null && row.UnitId != catalogue.Id && (usedUnits.Contains(row.Id) || (row == baseRow && hasMovements)))
            {
                return (null, AppError.Invalid("Units", "The unit is in use: add a new unit instead of replacing it."));
            }

            if (row is not null && !input.IsBase && row.Factor != factor && usedUnits.Contains(row.Id))
            {
                return (null, AppError.Invalid("Factor", "The unit is in use: its factor cannot change."));
            }

            if (plans.Any(p => p.Catalogue.Id == catalogue.Id))
            {
                return (null, AppError.Invalid("Units", "A unit appears once per item."));
            }

            if (!DocumentCodes.TypedAllowed(input.Code))
            {
                return (null, AppError.InvalidFields(["Unit code"]));
            }

            var code = DocumentCodes.Typed(input.Code) ?? row?.Code
                ?? await InputItemUnitRules.FreeCodeAsync(
                    InputItemUnitRules.DefaultCode(item.Code, catalogue.Name),
                    async candidate => takenCodes.Contains(candidate) || await codes.DuplicateAsync(candidate, null, null, cancellationToken) is not null);
            if (!takenCodes.Add(code))
            {
                return (null, AppError.Invalid("Unit code", "The code is used twice."));
            }

            if (await codes.DuplicateAsync(code, item.Id, row?.Id, cancellationToken) is { } duplicate)
            {
                return (null, duplicate);
            }

            plans.Add(new UnitPlan(row, catalogue, factor, code, input.IsBase));
        }

        if (existing is not null && existing.Where(u => !seenRows.Contains(u.Id)).Any(u => u.IsBase || usedUnits.Contains(u.Id)))
        {
            return (null, AppError.Invalid("Units", "A unit in use, or the base unit, cannot be removed."));
        }

        return (plans, null);
    }

    /// <summary>The catalogue unit an input names: by id, or by name (found regardless of letter case, else created).</summary>
    private async Task<InputUnit?> CatalogueUnitAsync(ItemUnitInput input, CancellationToken cancellationToken)
    {
        if (input.UnitId is { } unitId)
        {
            return (await units.GetByIdsAsync([unitId], cancellationToken)).FirstOrDefault();
        }

        var name = input.UnitName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > InputItemUnitRules.MaxNameLength)
        {
            return null;
        }

        if (await units.FindByNameAsync(name, null, cancellationToken) is { } found)
        {
            return found;
        }

        var created = new InputUnit { Name = name };
        units.Add(created);
        return created;
    }

    private static InputItemUnit NewUnit(FarmingInputItem item, UnitPlan plan)
    {
        var unit = new InputItemUnit { ItemId = item.Id, UnitId = plan.Catalogue.Id, Unit = plan.Catalogue, Factor = plan.Factor, Code = plan.Code, IsBase = plan.IsBase };
        item.Units.Add(unit);
        return unit;
    }

    /// <summary>Changes the kept units, adds the new ones and removes the others; the base unit name kept on the item follows.</summary>
    private void ApplyUnits(FarmingInputItem item, IReadOnlyList<UnitPlan> plans)
    {
        var kept = plans.Where(p => p.Row is not null).Select(p => p.Row!.Id).ToHashSet();
        foreach (var removed in item.Units.Where(u => !kept.Contains(u.Id)).ToList())
        {
            item.Units.Remove(removed);
            items.RemoveUnit(removed);
        }

        foreach (var plan in plans)
        {
            if (plan.Row is { } row)
            {
                row.UnitId = plan.Catalogue.Id;
                row.Unit = plan.Catalogue;
                row.Factor = plan.Factor;
                row.Code = plan.Code;
            }
            else
            {
                items.AddUnit(NewUnit(item, plan));
            }
        }

        item.Unit = plans.Single(p => p.IsBase).Catalogue.Name;
    }

    private static object Snapshot(FarmingInputItem i, IReadOnlyList<UnitPlan>? plans) => new
    {
        i.Code, i.Name, i.Group, i.ItemGroupId, i.Unit, i.Description, i.Notes, i.MinQuantity, i.MaxQuantity, i.CostPrice, i.ProductId,
        Units = plans is null
            ? i.Units.OrderByDescending(u => u.IsBase).ThenBy(u => u.Factor).Select(u => new { Unit = u.Unit?.Name ?? string.Empty, u.Factor, u.Code, u.IsBase }).ToList()
            : plans.OrderByDescending(p => p.IsBase).ThenBy(p => p.Factor).Select(p => new { Unit = p.Catalogue.Name, p.Factor, p.Code, p.IsBase }).ToList(),
    };

    /// <summary>The filter of a list query; an error for an unknown sort or an upside-down period.</summary>
    private static Result<InputItemFilter> FilterOf(FarmingInputItemListQuery query)
    {
        InputItemSort? sort = query.SortBy switch
        {
            null or "code" => InputItemSort.Code,
            "name" => InputItemSort.Name,
            "group" => InputItemSort.Group,
            "onHand" => InputItemSort.OnHand,
            "costPrice" => InputItemSort.CostPrice,
            "lastPurchasePrice" => InputItemSort.LastPurchasePrice,
            "stockValue" => InputItemSort.StockValue,
            "createdAt" => InputItemSort.CreatedAt,
            "status" => InputItemSort.Status,
            _ => null,
        };
        if (sort is null)
        {
            return AppError.Invalid("Sort by");
        }

        if (query.SortDir is not (null or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction");
        }

        if (query.CreatedFrom > query.CreatedTo)
        {
            return AppError.Invalid("Created");
        }

        if (query.Group is { } group && !Enum.IsDefined(group))
        {
            return AppError.Invalid("Group");
        }

        if (!Enum.IsDefined(query.Stock))
        {
            return AppError.Invalid("Stock");
        }

        if (!Enum.IsDefined(query.Status))
        {
            return AppError.Invalid("Status");
        }

        int? statusId = query.Status switch
        {
            InputItemStatusFilter.InUse => StatusIds.Usage.InUse,
            InputItemStatusFilter.Discontinued => StatusIds.Usage.Discontinued,
            _ => null,
        };
        var text = DisplayNames.Normalize(query.Search);
        var descending = sort == InputItemSort.Code ? query.SortDir != "asc" : query.SortDir == "desc";
        return new InputItemFilter(
            text.Length == 0 ? null : text, query.Group, query.ItemGroupId, query.Stock, query.SupplierId,
            query.CreatedFrom is { } from ? BusinessClock.FromLocal(from.ToDateTime(TimeOnly.MinValue)) : null,
            query.CreatedTo is { } to ? BusinessClock.FromLocal(to.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null,
            statusId, sort.Value, descending);
    }

    /// <summary>The detail of an item: its list columns, its photos, and whether its base unit is fixed.</summary>
    private async Task<FarmingInputItemView> ViewAsync(FarmingInputItem item, CancellationToken cancellationToken)
    {
        var onHand = await ledger.OnHandAsync([item.Id], cancellationToken);
        var row = (await ListItemsAsync([new InputItemRow(item, onHand[item.Id])], withPhotos: false, cancellationToken))[0];
        var photos = new List<ItemPhotoView>();
        foreach (var photo in item.Photos.OrderBy(p => p.SortOrder))
        {
            photos.Add(new ItemPhotoView(photo.Id, await storage.GetReadUrlAsync(photo.StorageKey, cancellationToken: cancellationToken)));
        }

        return FarmingInputItemView.Of(
            row, photos, await items.HasMovementsAsync(item.Id, cancellationToken), await stock.OpeningQuantityAsync(item.Id, cancellationToken));
    }

    private async Task<IReadOnlyList<FarmingInputItemListItem>> ListItemsAsync(IReadOnlyList<InputItemRow> rows, bool withPhotos, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.Item.Id).ToList();
        var used = await items.UsedAmongAsync(ids, cancellationToken);
        var usedUnits = await items.UsedUnitsAmongAsync(ids, cancellationToken);
        var creators = rows.Select(r => r.Item.CreatedBy).OfType<Guid>().Distinct().ToList();
        var names = creators.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(creators, cancellationToken);
        var result = new List<FarmingInputItemListItem>(rows.Count);
        foreach (var (i, onHand) in rows)
        {
            var photo = withPhotos && i.Photos.OrderBy(p => p.SortOrder).FirstOrDefault() is { } first
                ? await storage.GetReadUrlAsync(first.StorageKey, cancellationToken: cancellationToken)
                : null;
            result.Add(new FarmingInputItemListItem(
                i.Id, i.Code, i.Name, i.Group, i.ItemGroupId, i.ItemGroup?.Name, i.Unit,
                i.Units.OrderByDescending(u => u.IsBase).ThenBy(u => u.Factor).Select(u => new ItemUnitView(u.Id, u.UnitId, u.Unit.Name, u.Factor, u.Code, u.IsBase, usedUnits.Contains(u.Id))).ToList(),
                i.Description, i.Notes, photo, i.MinQuantity, i.MaxQuantity, i.CostPrice, i.LastPurchasePrice, onHand, onHand * (i.CostPrice ?? 0m),
                used.Contains(i.Id), i.StatusId, statusCatalog.DisplayNameOf(i.StatusId), i.CreatedAt, i.CreatedBy is { } id ? names.GetValueOrDefault(id) : null,
                RowVersionGuard.Encode(i.RowVersion), i.ProductId, i.Product?.Name));
        }

        return result;
    }
}


