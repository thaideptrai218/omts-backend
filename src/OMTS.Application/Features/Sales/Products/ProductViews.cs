using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>The product answers: the list columns, the units, the first photo and the kg on hand. A trader sees no price and no stock.</summary>
public sealed class ProductViews(IProductRepository products, IAccountRepository accounts, IFileStorage storage, IStatusCatalog statusCatalog)
{
    public async Task<IReadOnlyList<ProductView>> ListAsync(IReadOnlyList<ProductRow> rows, bool withPhotos, bool hidePrice, bool hideStock, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.Product.Id).ToList();
        var used = await products.UsedAmongAsync(ids, cancellationToken);
        var creators = rows.Select(r => r.Product.CreatedBy).OfType<Guid>().Distinct().ToList();
        var names = creators.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(creators, cancellationToken);
        var result = new List<ProductView>(rows.Count);
        foreach (var (p, onHand) in rows)
        {
            var photo = withPhotos && p.Photos.OrderBy(x => x.SortOrder).FirstOrDefault() is { } first
                ? await storage.GetReadUrlAsync(first.StorageKey, cancellationToken: cancellationToken)
                : null;
            result.Add(ViewOf(p, onHand, used.Contains(p.Id), photo, names, hidePrice, hideStock));
        }

        return result;
    }

    /// <summary>The detail of a product: its list columns and every photo.</summary>
    public async Task<ProductDetailView> DetailAsync(Product product, decimal onHand, bool hidePrice, bool hideStock, CancellationToken cancellationToken)
    {
        var used = await products.IsUsedAsync(product.Id, cancellationToken);
        var names = product.CreatedBy is { } creator ? await accounts.GetDisplayNamesAsync([creator], cancellationToken) : new Dictionary<Guid, string>();
        var photos = new List<ProductPhotoView>();
        foreach (var photo in product.Photos.OrderBy(p => p.SortOrder))
        {
            photos.Add(new ProductPhotoView(photo.Id, await storage.GetReadUrlAsync(photo.StorageKey, cancellationToken: cancellationToken)));
        }

        return ProductDetailView.Of(ViewOf(product, onHand, used, null, names, hidePrice, hideStock), photos);
    }

    private ProductView ViewOf(Product p, decimal onHand, bool isUsed, string? photoUrl, IReadOnlyDictionary<Guid, string> creatorNames, bool hidePrice, bool hideStock)
    {
        var units = p.Units.OrderByDescending(u => u.IsBase).ThenBy(u => u.Factor).Select(u => new ProductUnitView(u.Id, u.UnitId, u.Unit?.Name ?? string.Empty, u.Factor, u.Code, u.IsBase, hidePrice ? null : u.SalePrice)).ToList();
        var baseName = units.FirstOrDefault(u => u.IsBase)?.Name;
        return new ProductView(
            p.Id, p.Name, hidePrice ? null : p.UnitPrice, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), RowVersionGuard.Encode(p.RowVersion),
            p.Code, p.CategoryId, p.Category?.Name, string.IsNullOrEmpty(baseName) ? ProductRules.BaseUnitName : baseName, units, p.Description, p.Note, photoUrl,
            p.MinKg, p.MaxKg, hideStock ? null : onHand, isUsed, p.CreatedAt, p.CreatedBy is { } id ? creatorNames.GetValueOrDefault(id) : null);
    }
}
