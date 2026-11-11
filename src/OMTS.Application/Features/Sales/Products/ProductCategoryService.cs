using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>
/// The user groups of the products ("Nhóm hàng"): a name unique regardless of letter case, an order the Owner sets, two levels at most, and the number of products in each. Reading follows the product catalogue,
/// writes are Owner only (API filter). A group is deleted only when no child group and no product (that is not deleted) belongs to it; deleted products let go of it.
/// </summary>
public sealed class ProductCategoryService(
    IProductCategoryRepository categories, ProductAccess access, RowVersionGuard rowVersions, AuditLogService audit, IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(ProductCategory);
    private const string ObjectName = "Product group";
    public const int MaxNameLength = 100;
    public const int MaxCategories = 200;

    public async Task<Result<IReadOnlyList<ProductCategoryView>>> ListAsync(CancellationToken cancellationToken)
    {
        if (!ProductAccess.IsCatalogueReader(await access.ReaderAsync(cancellationToken)))
        {
            return AppError.Forbidden;
        }

        return Result<IReadOnlyList<ProductCategoryView>>.Success(await ViewsAsync(cancellationToken));
    }

    public async Task<Result<ProductCategoryView>> CreateAsync(SaveProductCategoryRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return AppError.InvalidFields(["Name"]);
        }

        var all = await categories.ListAsync(cancellationToken);
        if (all.Count >= MaxCategories)
        {
            return AppError.Invalid("Name", $"At most {MaxCategories} groups.");
        }

        if (ParentError(request.ParentId, null, all) is { } parentError)
        {
            return parentError;
        }

        if (await categories.FindByNameAsync(name, null, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        var category = new ProductCategory { Name = name, ParentId = request.ParentId, SortOrder = all.Select(g => g.SortOrder).DefaultIfEmpty(-1).Max() + 1 };
        categories.Add(category);
        audit.Record(new AuditEntry("ProductCategory.Created", RecordType) { RecordId = category.Id, RecordCode = category.Name, After = new { category.Name, category.SortOrder, category.ParentId } });
        if (await SaveAsync(name, cancellationToken) is { } error)
        {
            return error;
        }

        return new ProductCategoryView(category.Id, category.Name, category.SortOrder, 0, RowVersionGuard.Encode(category.RowVersion), category.ParentId);
    }

    /// <summary>MSG-GEN-01 (field Parent) when the parent is not a top-level group, is the group itself, or the group already has children: the tree is two levels deep at most and never loops.</summary>
    private static AppError? ParentError(Guid? parentId, Guid? selfId, IReadOnlyList<ProductCategory> all)
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

    public async Task<Result<ProductCategoryView>> UpdateAsync(Guid categoryId, SaveProductCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await categories.GetForEditAsync(categoryId, cancellationToken);
        if (category is null)
        {
            return AppError.NotFound;
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return AppError.InvalidFields(["Name"]);
        }

        if (await rowVersions.CheckAsync(category, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (await categories.FindByNameAsync(name, category.Id, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        if (ParentError(request.ParentId, category.Id, await categories.ListAsync(cancellationToken)) is { } parentError)
        {
            return parentError;
        }

        if (name != category.Name || request.ParentId != category.ParentId)
        {
            audit.Record(new AuditEntry("ProductCategory.Updated", RecordType)
            {
                RecordId = category.Id, RecordCode = name, Before = new { category.Name, category.ParentId }, After = new { Name = name, request.ParentId },
            });
            category.Name = name;
            category.ParentId = request.ParentId;
            if (await SaveAsync(name, cancellationToken) is { } error)
            {
                return error;
            }
        }

        return (await ViewsAsync(cancellationToken)).First(v => v.Id == category.Id);
    }

    /// <summary>Puts the groups in the order of <c>ids</c>, which must name every group once.</summary>
    public async Task<Result<IReadOnlyList<ProductCategoryView>>> ReorderAsync(ReorderProductCategoriesRequest request, CancellationToken cancellationToken)
    {
        var all = await categories.GetAllForEditAsync(cancellationToken);
        var ids = request.Ids ?? [];
        if (ids.Count != all.Count || ids.Distinct().Count() != ids.Count || !all.Select(g => g.Id).ToHashSet().SetEquals(ids))
        {
            return AppError.InvalidFields(["Ids"]);
        }

        var byId = all.ToDictionary(g => g.Id);
        var changed = false;
        for (var order = 0; order < ids.Count; order++)
        {
            var category = byId[ids[order]];
            if (category.SortOrder != order)
            {
                category.SortOrder = order;
                changed = true;
            }
        }

        if (changed)
        {
            audit.Record(new AuditEntry("ProductCategory.Reordered", RecordType) { After = new { Order = ids.Select(id => byId[id].Name).ToList() } });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<IReadOnlyList<ProductCategoryView>>.Success(await ViewsAsync(cancellationToken));
    }

    /// <summary>Deletes a group no product (that is not deleted) belongs to; MSG-GEN-06 with the number of products otherwise.</summary>
    public async Task<Result<Done>> DeleteAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await categories.GetForEditAsync(categoryId, cancellationToken);
        if (category is null)
        {
            return AppError.NotFound;
        }

        if ((await categories.ListAsync(cancellationToken)).Count(g => g.ParentId == category.Id) is var children and > 0)
        {
            return StillUsed(children, "child groups");
        }

        if ((await categories.ProductCountsAsync(cancellationToken)).GetValueOrDefault(category.Id) is var count and > 0)
        {
            return StillUsed(count, "products");
        }

        foreach (var hidden in await categories.HiddenProductsAsync(category.Id, cancellationToken))
        {
            hidden.CategoryId = null;
        }

        categories.Remove(category);
        audit.Record(new AuditEntry("ProductCategory.Deleted", RecordType) { RecordId = category.Id, RecordCode = category.Name, Before = new { category.Name } });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ForeignKeyViolationException)
        {
            // A product joined the group after the check.
            unitOfWork.DiscardChanges();
            return StillUsed((await categories.ProductCountsAsync(cancellationToken)).GetValueOrDefault(categoryId, 1), "products");
        }

        return Done.Value;
    }

    private static AppError StillUsed(int count, string what) => AppError.Conflict(
        UserMessages.StillInUse(1, $"{what} ({count})"),
        new Dictionary<string, object?> { ["references"] = new[] { new { Name = what == "products" ? "Products" : "Child groups", Count = count } } });

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
            return await categories.FindByNameAsync(name, null, cancellationToken) is { } other
                ? AppError.AlreadyExists(ObjectName, other.Id, "Name")
                : AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }
    }

    private async Task<IReadOnlyList<ProductCategoryView>> ViewsAsync(CancellationToken cancellationToken)
    {
        var counts = await categories.ProductCountsAsync(cancellationToken);
        var all = await categories.ListAsync(cancellationToken);
        // A parent counts its own products and those of its children.
        return all.Select(g => new ProductCategoryView(
            g.Id, g.Name, g.SortOrder, counts.GetValueOrDefault(g.Id) + all.Where(c => c.ParentId == g.Id).Sum(c => counts.GetValueOrDefault(c.Id)), RowVersionGuard.Encode(g.RowVersion), g.ParentId)).ToList();
    }
}
