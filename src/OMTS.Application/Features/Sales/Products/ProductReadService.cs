using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>
/// The reads of the products. The plain list is what pickers use: the Owner reads all of them (SALE-11), who sells or sends requests lists those In Use, the Zone Manager with their prices (SALE-05 step 1), the
/// trader with no price (SALE-01 step 1, BR-SALE-01); whoever records harvests picks the products of a lot from the same list, with no price and no stock. The paged <c>search</c> and the detail with the kg on hand are the catalogue screen: the Owner, the Zone Manager and the Accountant. Others have no right.
/// </summary>
public sealed class ProductReadService(
    IProductRepository products,
    IProductListQuery list,
    IProductStockRepository stock,
    ProductViews views,
    ProductAccess access,
    ProductCodeService codes)
{
    private const int MaxPageSize = 100;

    /// <summary>For the route filter, which answers before the request is read, like the permission filter of other routes.</summary>
    public async Task<bool> MayReadAsync(CancellationToken cancellationToken) => await access.ReaderAsync(cancellationToken) != ProductReader.None;

    /// <summary>The Owner may filter by status; the others always get those In Use. Longer than any name, a search finds nothing. Deleted products are not listed.</summary>
    public async Task<Result<IReadOnlyList<ProductView>>> ListAsync(string? search, int? statusId, CancellationToken cancellationToken)
    {
        var reader = await access.ReaderAsync(cancellationToken);
        if (reader == ProductReader.None)
        {
            return AppError.Forbidden;
        }

        var text = DisplayNames.Normalize(search);
        if (text.Length > DisplayNames.MaxLength)
        {
            return new List<ProductView>();
        }

        var found = await products.ListAsync(text, reader == ProductReader.Owner ? statusId : StatusIds.Usage.InUse, cancellationToken);
        var noPriceNoStock = reader is ProductReader.Trader or ProductReader.Picker;
        var onHand = noPriceNoStock ? new Dictionary<Guid, decimal>() : await stock.OnHandAsync(found.Select(p => p.Id).ToList(), cancellationToken);
        var rows = found.Select(p => new ProductRow(p, onHand.GetValueOrDefault(p.Id))).ToList();
        return Result<IReadOnlyList<ProductView>>.Success(await views.ListAsync(rows, withPhotos: false, noPriceNoStock, noPriceNoStock, cancellationToken));
    }

    /// <summary>The paged catalogue with the kg on hand, filters, sort and the total kg of every match.</summary>
    public async Task<Result<ProductPage>> SearchAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        var reader = await access.ReaderAsync(cancellationToken);
        if (!ProductAccess.IsCatalogueReader(reader))
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
        var (rows, total, totalKg) = await list.SearchAsync(filter.Value!, page, pageSize, cancellationToken);
        return new ProductPage(await views.ListAsync(rows, withPhotos: true, hidePrice: false, hideStock: false, cancellationToken), page, pageSize, total, new ProductListSummary(totalKg));
    }

    /// <summary>Every matching product for the export, with the same filters as the list.</summary>
    internal async Task<Result<IReadOnlyList<ProductView>>> ExportRowsAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        if (!ProductAccess.IsCatalogueReader(await access.ReaderAsync(cancellationToken)))
        {
            return AppError.Forbidden;
        }

        var filter = FilterOf(query);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        return Result<IReadOnlyList<ProductView>>.Success(
            await views.ListAsync(await list.ListAsync(filter.Value!, cancellationToken), withPhotos: false, hidePrice: false, hideStock: false, cancellationToken));
    }

    /// <summary>A Discontinued or deleted product can still be viewed (GBR-01), for example on an older invoice.</summary>
    public async Task<Result<ProductDetailView>> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        var reader = await access.ReaderAsync(cancellationToken);
        if (reader == ProductReader.None)
        {
            return AppError.Forbidden;
        }

        if (await products.GetForEditAsync(productId, cancellationToken) is not { } product)
        {
            return AppError.NotFound;
        }

        var isTrader = reader is ProductReader.Trader or ProductReader.Picker;
        var onHand = isTrader ? 0m : (await stock.OnHandAsync([product.Id], cancellationToken))[product.Id];
        return await views.DetailAsync(product, onHand, isTrader, isTrader, cancellationToken);
    }

    /// <summary>The code a new product would get (SP000001), which the Owner may replace.</summary>
    public async Task<NewProductProposal> ProposeAsync(CancellationToken cancellationToken) => new(await codes.PeekAsync(cancellationToken));

    /// <summary>The filter of a list query; an error for an unknown sort.</summary>
    private static Result<ProductFilter> FilterOf(ProductListQuery query)
    {
        ProductSort? sort = query.SortBy switch
        {
            null or "code" => ProductSort.Code,
            "name" => ProductSort.Name,
            "category" => ProductSort.Category,
            "unitPrice" => ProductSort.UnitPrice,
            "onHand" => ProductSort.OnHand,
            "createdAt" => ProductSort.CreatedAt,
            "status" => ProductSort.Status,
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
            ProductStatusFilter.InUse => StatusIds.Usage.InUse,
            ProductStatusFilter.Discontinued => StatusIds.Usage.Discontinued,
            _ => null,
        };
        var text = DisplayNames.Normalize(query.Search);
        var descending = sort == ProductSort.Code ? query.SortDir != "asc" : query.SortDir == "desc";
        return new ProductFilter(text.Length == 0 ? null : text, query.CategoryId, query.Stock, statusId, sort.Value, descending);
    }
}
