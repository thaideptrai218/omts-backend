using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>
/// The product catalogue of the Cooperative Owner, who alone writes it (API filter, BR-SALE-05). Code (typed, or SP000001) and name are unique, discontinued and deleted products included; only an In Use
/// product is edited; a Discontinued one can be resumed (GBR-01). Every product has kg as its base unit (price, stock and invoices are per kg) and any number of extra units (lồng, thùng…) with a factor in kg,
/// taken from the shared unit list. A price change writes one row of the price history. A product nothing refers to is removed; one that was used is hidden (Discontinued, in no list). Reads: <see cref="ProductReadService"/>.
/// </summary>
public sealed class ProductService(
    IProductRepository products,
    IInputUnitRepository units,
    IProductCategoryRepository categories,
    IProductStockRepository stock,
    ProductCodeService codes,
    ProductViews views,
    IFileStorage storage,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(Product);
    internal const string ObjectName = "Product";
    private const string PhotoCategory = "product-photos";

    /// <summary>The largest amount the column holds (18 digits).</summary>
    private const decimal MaxUnitPrice = 999_999_999_999_999_999m;
    private const decimal MaxKg = 1_000_000_000m;

    private readonly Dictionary<string, InputUnit> stagedUnits = new(StringComparer.OrdinalIgnoreCase);

    public async Task<Result<ProductSaved>> CreateAsync(SaveProductRequest request, CancellationToken cancellationToken)
    {
        var staged = await StageCreateAsync(request, cancellationToken);
        if (staged.Error is not null)
        {
            return staged.Error;
        }

        if (await SaveAsync(staged.Product!.Name, null, cancellationToken) is { } error)
        {
            return error;
        }

        return await SavedAsync(staged.Product, cancellationToken);
    }

    /// <summary>Saves what <see cref="StageCreateAsync"/> staged, all in one save: MSG-GEN-02 when a parallel save took a name or code meanwhile, and then nothing is saved.</summary>
    internal async Task<AppError?> CommitStagedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            return AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }
    }

    /// <summary>Forgets every product staged and not saved.</summary>
    internal void DiscardStaged()
    {
        unitOfWork.DiscardChanges();
        stagedUnits.Clear();
    }

    /// <summary>Checks a new product and adds it (with its units, price history and audit row) to the unit of work without saving; the import stages every row and saves once.</summary>
    internal async Task<(Product? Product, AppError? Error)> StageCreateAsync(SaveProductRequest request, CancellationToken cancellationToken)
    {
        var (values, invalid) = await ValidateAsync(request, cancellationToken);
        if (invalid.Count > 0)
        {
            return (null, AppError.InvalidFields(invalid));
        }

        if (await DuplicateAsync(values.Name, null, cancellationToken) is { } duplicate)
        {
            return (null, duplicate);
        }

        var (code, codeError) = await codes.ForNewAsync(request.Code, cancellationToken);
        if (codeError is not null)
        {
            return (null, codeError);
        }

        var product = new Product { Code = code! };
        Apply(product, values);
        var planned = await PlanUnitsAsync(request, product, cancellationToken);
        if (planned.Error is not null)
        {
            return (null, planned.Error);
        }

        products.Add(product);
        foreach (var plan in planned.Plans!)
        {
            products.AddUnit(NewUnit(product, plan));
        }

        foreach (var unit in product.Units)
        {
            products.AddPriceChange(NewPriceChange(product, unit, null, unit.SalePrice));
        }

        audit.Record(new AuditEntry("Product.Created", RecordType) { RecordId = product.Id, RecordCode = product.Code, After = Snapshot(product, planned.Plans!) });
        return (product, null);
    }

    /// <summary>SALE-11.1: an In Use product only. A new price applies to invoices created after the save (BR-SALE-05) and adds a row to the price history.</summary>
    public async Task<Result<ProductSaved>> UpdateAsync(Guid productId, SaveProductRequest request, CancellationToken cancellationToken)
    {
        var product = await products.GetForEditAsync(productId, cancellationToken);
        if (product is not { IsHidden: false })
        {
            return AppError.NotFound;
        }

        if (product.StatusId != StatusIds.Usage.InUse)
        {
            return WrongStatus(product);
        }

        var (values, invalid) = await ValidateAsync(request, cancellationToken);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await DuplicateAsync(values.Name, product.Id, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        var (code, codeError) = await codes.ForEditAsync(request.Code, product.Id, product.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        if (await rowVersions.CheckAsync(product, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(product, null);
        var pricesBefore = product.Units.ToDictionary(u => u.Id, u => u.SalePrice);
        product.Code = code!;
        Apply(product, values);
        var planned = await PlanUnitsAsync(request, product, cancellationToken);
        if (planned.Error is not null)
        {
            return planned.Error;
        }

        if (planned.Plans is { } plans)
        {
            ApplyUnits(product, plans);
        }

        // The base unit carries the list price: it is kept equal to it, whichever way the units were saved.
        foreach (var baseUnit in product.Units.Where(u => u.IsBase))
        {
            baseUnit.SalePrice = product.UnitPrice;
        }

        // One history row per unit whose price changed, and the first row of a unit that was added.
        foreach (var unit in product.Units)
        {
            if (!pricesBefore.TryGetValue(unit.Id, out var was))
            {
                products.AddPriceChange(NewPriceChange(product, unit, null, unit.SalePrice));
            }
            else if (was != unit.SalePrice)
            {
                products.AddPriceChange(NewPriceChange(product, unit, was, unit.SalePrice));
            }
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(product, null));
        if (changedAfter is not null)
        {
            // Units, photos and price changes are rows of their own: the product still gets a new row version.
            unitOfWork.MarkChanged(product);
            audit.Record(new AuditEntry("Product.Updated", RecordType) { RecordId = product.Id, RecordCode = product.Code, Before = changedBefore, After = changedAfter });
        }

        if (await SaveAsync(values.Name, product.Id, cancellationToken) is { } error)
        {
            return error;
        }

        return await SavedAsync(product, cancellationToken);
    }

    public Task<Result<ProductDetailView>> DiscontinueAsync(Guid productId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(productId, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, "Product.Discontinued", cancellationToken);

    /// <summary>SALE-11.2: back to In Use with the same code, name, price and history.</summary>
    public Task<Result<ProductDetailView>> ResumeAsync(Guid productId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(productId, StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, "Product.Resumed", cancellationToken);

    /// <summary>Removes a product nothing refers to; hides one that has history (Discontinued, in no list), which keeps its code and name taken.</summary>
    public async Task<Result<ProductDeleted>> DeleteAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await products.GetForEditAsync(productId, cancellationToken);
        if (product is not { IsHidden: false })
        {
            return AppError.NotFound;
        }

        var snapshot = Snapshot(product, null);
        if (!await products.IsUsedAsync(product.Id, cancellationToken))
        {
            var photoKeys = product.Photos.Select(p => p.StorageKey).ToList();
            foreach (var change in await products.PriceHistoryForEditAsync(product.Id, cancellationToken))
            {
                products.RemovePriceChange(change);
            }

            foreach (var photo in product.Photos.ToList())
            {
                products.RemovePhoto(photo);
            }

            foreach (var unit in product.Units.ToList())
            {
                products.RemoveUnit(unit);
            }

            products.Remove(product);
            audit.Record(new AuditEntry("Product.Deleted", RecordType) { RecordId = product.Id, RecordCode = product.Code, Before = snapshot });
            await unitOfWork.SaveChangesAsync(cancellationToken);
            foreach (var key in photoKeys)
            {
                await storage.DeleteAsync(key, cancellationToken);
            }

            return new ProductDeleted(product.Id, product.Code, true);
        }

        var from = product.StatusId;
        product.IsHidden = true;
        product.StatusId = StatusIds.Usage.Discontinued;
        unitOfWork.MarkChanged(product);
        audit.Record(new AuditEntry("Product.Hidden", RecordType)
        {
            RecordId = product.Id, RecordCode = product.Code, Before = new { Status = statusCatalog.CodeOf(from), IsHidden = false }, After = new { Status = statusCatalog.CodeOf(product.StatusId), IsHidden = true },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ProductDeleted(product.Id, product.Code, false);
    }

    /// <summary>JPEG or PNG of at most 2 MB, added to the photos (at most five); a failed upload leaves the product unchanged (MSG-GEN-03).</summary>
    public async Task<Result<ProductDetailView>> AddPhotoAsync(Guid productId, IncomingFile? file, CancellationToken cancellationToken)
    {
        var product = await products.GetForEditAsync(productId, cancellationToken);
        if (product is not { IsHidden: false })
        {
            return AppError.NotFound;
        }

        var validated = FileValidator.Validate(file is null ? [] : [file], FileRules.ItemPhoto);
        if (file is null || !validated.IsValid)
        {
            return AppError.Invalid(validated.InvalidFileName ?? "Photo", validated.Problem);
        }

        if (product.Photos.Count >= ProductRules.MaxPhotos)
        {
            return AppError.Invalid("Photos", $"At most {ProductRules.MaxPhotos} photos.");
        }

        var uploaded = await storage.UploadAsync(validated.Files[0], PhotoCategory, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.ServiceUnavailable, UserMessages.UploadFailed with { LegacyCode = MessageCodes.Legacy.Msg04 });
        }

        var photo = new ProductPhoto { ProductId = product.Id, StorageKey = uploaded.Key!, SortOrder = product.Photos.Select(p => p.SortOrder).DefaultIfEmpty(-1).Max() + 1 };
        products.AddPhoto(photo);
        product.Photos.Add(photo);
        audit.Record(new AuditEntry("Product.Updated", RecordType)
        {
            RecordId = product.Id, RecordCode = product.Code, Before = new { Photos = product.Photos.Count - 1 }, After = new { Photos = product.Photos.Count },
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

        return await DetailAsync(product, cancellationToken);
    }

    public async Task<Result<ProductDetailView>> RemovePhotoAsync(Guid productId, Guid photoId, CancellationToken cancellationToken)
    {
        var product = await products.GetForEditAsync(productId, cancellationToken);
        if (product is not { IsHidden: false } || product.Photos.FirstOrDefault(p => p.Id == photoId) is not { } photo)
        {
            return AppError.NotFound;
        }

        product.Photos.Remove(photo);
        products.RemovePhoto(photo);
        audit.Record(new AuditEntry("Product.Updated", RecordType)
        {
            RecordId = product.Id, RecordCode = product.Code, Before = new { Photos = product.Photos.Count + 1 }, After = new { Photos = product.Photos.Count },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(photo.StorageKey, cancellationToken);
        return await DetailAsync(product, cancellationToken);
    }

    private async Task<Result<ProductDetailView>> ChangeStatusAsync(Guid productId, int fromStatusId, int toStatusId, string action, CancellationToken cancellationToken)
    {
        var product = await products.GetForEditAsync(productId, cancellationToken);
        if (product is not { IsHidden: false })
        {
            return AppError.NotFound;
        }

        if (product.StatusId != fromStatusId)
        {
            return WrongStatus(product);
        }

        product.StatusId = toStatusId;
        audit.Record(new AuditEntry(action, RecordType)
        {
            RecordId = product.Id,
            RecordCode = product.Code,
            Before = new { Status = statusCatalog.CodeOf(fromStatusId) },
            After = new { Status = statusCatalog.CodeOf(toStatusId) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await DetailAsync(product, cancellationToken);
    }

    /// <summary>MSG-GEN-02 with the id of the other product that has this name, which the Owner may open or resume.</summary>
    private async Task<AppError?> DuplicateAsync(string name, Guid? exceptProductId, CancellationToken cancellationToken) =>
        await products.FindByNameAsync(name, exceptProductId, cancellationToken) is { } same ? AppError.AlreadyExists(ObjectName, same.Id, "Name") : null;

    /// <summary>The second of two parallel saves with one code, name or unit code gets MSG-GEN-02, like the pre-check.</summary>
    private async Task<AppError?> SaveAsync(string name, Guid? exceptProductId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            return await DuplicateAsync(name, exceptProductId, cancellationToken) ?? AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }
    }

    private AppError WrongStatus(Product product) =>
        AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {product.Code}", statusCatalog.DisplayNameOf(product.StatusId)));

    private sealed record Values(string Name, decimal UnitPrice, Guid? CategoryId, string? Description, string? Note, decimal? MinKg, decimal? MaxKg);

    private async Task<(Values Values, List<string> Invalid)> ValidateAsync(SaveProductRequest request, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        var name = DisplayNames.Normalize(request.Name);
        if (name.Length is 0 or > DisplayNames.MaxLength)
        {
            invalid.Add("Name");
        }

        if (request.UnitPrice is not { } price || price <= 0 || price > MaxUnitPrice || decimal.Truncate(price) != price)
        {
            invalid.Add("Unit price");
        }

        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        string? Text(string? value, string field)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Length > 2000)
            {
                invalid.Add(field);
            }

            return trimmed.Length == 0 ? null : trimmed;
        }

        var description = Text(request.Description, "Description");
        var note = Text(request.Note, "Note");
        if (request.CategoryId is { } categoryId && await categories.GetForEditAsync(categoryId, cancellationToken) is null)
        {
            invalid.Add("Category");
        }

        bool Kg(decimal? value, string field)
        {
            if (value is { } v && (v < 0 || v > MaxKg || decimal.Round(v, 3) != v))
            {
                invalid.Add(field);
                return false;
            }

            return true;
        }

        if (Kg(request.MinKg, "Min kg") & Kg(request.MaxKg, "Max kg") && request.MinKg > request.MaxKg)
        {
            invalid.Add("Max kg");
        }

        return (new Values(name, request.UnitPrice ?? 0, request.CategoryId, description, note, request.MinKg, request.MaxKg), invalid);
    }

    private static void Apply(Product product, Values values)
    {
        product.Name = values.Name;
        product.UnitPrice = values.UnitPrice;
        product.CategoryId = values.CategoryId;
        product.Description = values.Description;
        product.Note = values.Note;
        product.MinKg = values.MinKg;
        product.MaxKg = values.MaxKg;
    }

    private ProductPriceChange NewPriceChange(Product product, ProductUnit unit, decimal? oldPrice, decimal newPrice) => new()
    {
        ProductId = product.Id, UnitId = unit.UnitId, UnitCode = unit.Code, UnitName = unit.Unit?.Name ?? string.Empty, OldPrice = oldPrice, NewPrice = newPrice, ChangedAt = timeProvider.GetUtcNow().ToOffset(BusinessClock.UtcOffset), ChangedByAccountId = currentUser.AccountId,
    };

    /// <summary>One unit of the product as it will be saved: an existing row (<see cref="Row"/>) or a new one, with its catalogue unit, factor and code.</summary>
    private sealed record UnitPlan(ProductUnit? Row, InputUnit Catalogue, decimal Factor, string Code, bool IsBase, decimal SalePrice);

    /// <summary>The units a save leaves the product with (null when an update keeps them), or the first error. The base unit is kg, always there, with factor 1.</summary>
    private async Task<(IReadOnlyList<UnitPlan>? Plans, AppError? Error)> PlanUnitsAsync(SaveProductRequest request, Product product, CancellationToken cancellationToken)
    {
        var existing = product.Units.ToList();
        var baseRow = existing.FirstOrDefault(u => u.IsBase);
        if (request.Units is null && baseRow is not null)
        {
            return (null, null);
        }

        var inputs = request.Units ?? [];
        if (inputs.Count >= ProductRules.MaxUnits || inputs.Any(i => i is null))
        {
            return (null, AppError.InvalidFields(["Units"]));
        }

        var kg = baseRow?.Unit ?? await CatalogueUnitAsync(new ProductUnitInput(null, null, ProductRules.BaseUnitName, 1m, null), cancellationToken);
        var takenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { product.Code };
        var baseCode = baseRow?.Code ?? await FreeCodeAsync(ProductRules.DefaultUnitCode(product.Code, ProductRules.BaseUnitName), takenCodes, cancellationToken);
        takenCodes.Add(baseCode);
        var basePrice = request.UnitPrice ?? 0m;
        var plans = new List<UnitPlan> { new(baseRow, kg!, 1m, baseCode, true, basePrice) };
        var seenRows = new HashSet<Guid>();
        foreach (var input in inputs)
        {
            var row = input.Id is { } id ? existing.FirstOrDefault(u => u.Id == id && !u.IsBase) : null;
            if ((input.Id is not null && row is null) || (row is not null && !seenRows.Add(row.Id)))
            {
                return (null, AppError.Invalid("Units", "A unit is named twice or is not one of the product's units; the base unit cannot be exchanged."));
            }

            var catalogue = await CatalogueUnitAsync(input, cancellationToken);
            if (catalogue is null || catalogue.Id == kg!.Id || ProductRules.IsBaseUnitName(catalogue.Name))
            {
                return (null, AppError.InvalidFields(["Units"]));
            }

            if (!InputItemUnitRules.FactorAllowed(input.Factor))
            {
                return (null, AppError.InvalidFields(["Factor"]));
            }

            if (plans.Any(p => p.Catalogue.Id == catalogue.Id))
            {
                return (null, AppError.Invalid("Units", "A unit appears once per product."));
            }

            if (!DocumentCodes.TypedAllowed(input.Code))
            {
                return (null, AppError.InvalidFields(["Unit code"]));
            }

            var code = DocumentCodes.Typed(input.Code) ?? row?.Code ?? await FreeCodeAsync(ProductRules.DefaultUnitCode(product.Code, catalogue.Name), takenCodes, cancellationToken);
            if (!takenCodes.Add(code))
            {
                return (null, AppError.Invalid("Unit code", "The code is used twice."));
            }

            if (await codes.DuplicateAsync(code, product.Id, row?.Id, cancellationToken) is { } duplicate)
            {
                return (null, duplicate);
            }

            if (input.SalePrice is { } typed && (typed <= 0 || typed > MaxUnitPrice || decimal.Truncate(typed) != typed))
            {
                return (null, AppError.InvalidFields(["Sale price"]));
            }

            // Left empty, a new unit starts at factor × the price per kg (whole VND, half up); an existing one keeps its price.
            var salePrice = input.SalePrice ?? row?.SalePrice ?? ProductRules.DefaultUnitPrice(input.Factor, basePrice);
            plans.Add(new UnitPlan(row, catalogue, input.Factor, code, false, salePrice));
        }

        return (plans, null);
    }

    private Task<string> FreeCodeAsync(string candidate, HashSet<string> taken, CancellationToken cancellationToken) =>
        InputItemUnitRules.FreeCodeAsync(candidate, async c => taken.Contains(c) || await codes.DuplicateAsync(c, null, null, cancellationToken) is not null);

    /// <summary>The catalogue unit an input names: by id, or by name (found regardless of letter case, else created in the shared unit list).</summary>
    private async Task<InputUnit?> CatalogueUnitAsync(ProductUnitInput input, CancellationToken cancellationToken)
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

        // A unit created earlier in this unit of work (an import stages many products) is not in the database yet.
        if (stagedUnits.TryGetValue(name, out var staged))
        {
            return staged;
        }

        var created = new InputUnit { Name = name };
        units.Add(created);
        stagedUnits[name] = created;
        return created;
    }

    private static ProductUnit NewUnit(Product product, UnitPlan plan)
    {
        var unit = new ProductUnit { ProductId = product.Id, UnitId = plan.Catalogue.Id, Unit = plan.Catalogue, Factor = plan.Factor, Code = plan.Code, IsBase = plan.IsBase, SalePrice = plan.SalePrice };
        product.Units.Add(unit);
        return unit;
    }

    /// <summary>Changes the kept units, adds the new ones and removes the others.</summary>
    private void ApplyUnits(Product product, IReadOnlyList<UnitPlan> plans)
    {
        var kept = plans.Where(p => p.Row is not null).Select(p => p.Row!.Id).ToHashSet();
        foreach (var removed in product.Units.Where(u => !kept.Contains(u.Id)).ToList())
        {
            product.Units.Remove(removed);
            products.RemoveUnit(removed);
        }

        foreach (var plan in plans)
        {
            if (plan.Row is { } row)
            {
                row.UnitId = plan.Catalogue.Id;
                row.Unit = plan.Catalogue;
                row.Factor = plan.Factor;
                row.Code = plan.Code;
                row.SalePrice = plan.SalePrice;
            }
            else
            {
                products.AddUnit(NewUnit(product, plan));
            }
        }
    }

    private static object Snapshot(Product p, IReadOnlyList<UnitPlan>? plans) => new
    {
        p.Code, p.Name, p.UnitPrice, p.CategoryId, p.Description, p.Note, p.MinKg, p.MaxKg,
        Units = plans is null
            ? p.Units.OrderByDescending(u => u.IsBase).ThenBy(u => u.Factor).Select(u => new { Unit = u.Unit?.Name ?? string.Empty, u.Factor, u.Code, u.IsBase, u.SalePrice }).ToList()
            : plans.OrderByDescending(x => x.IsBase).ThenBy(x => x.Factor).Select(x => new { Unit = x.Catalogue.Name, x.Factor, x.Code, x.IsBase, x.SalePrice }).ToList(),
    };

    private async Task<ProductDetailView> DetailAsync(Product product, CancellationToken cancellationToken) =>
        await views.DetailAsync(product, (await stock.OnHandAsync([product.Id], cancellationToken))[product.Id], hidePrice: false, hideStock: false, cancellationToken);

    private async Task<ProductSaved> SavedAsync(Product product, CancellationToken cancellationToken)
    {
        var saved = UserMessages.Saved(ObjectName);
        return new ProductSaved(await DetailAsync(product, cancellationToken), saved.Text, saved.Code);
    }
}
