using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>
/// Who reads the products: the Owner reads all of them (SALE-11); who sells or sends requests lists those offered, the Zone
/// Manager with their prices (SALE-05 step 1), the trader with no price (SALE-01 step 1, BR-SALE-01). Others have no right:
/// §I.4.4 gives the Accountant neither SALE-11 nor SALE-05.
/// </summary>
public sealed class ProductReadService(
    IProductRepository products,
    ICurrentUser currentUser,
    PermissionService permissions,
    IStatusCatalog statusCatalog)
{
    private enum Reader
    {
        None,
        Owner,
        Seller,
        Trader,
    }

    /// <summary>For the route filter, which answers before the request is read, like the permission filter of other routes.</summary>
    public async Task<bool> MayReadAsync(CancellationToken cancellationToken) => await ReaderAsync(cancellationToken) != Reader.None;

    /// <summary>The Owner may filter by status; the others always get those In Use. Longer than any name, a search finds nothing.</summary>
    public async Task<Result<IReadOnlyList<ProductView>>> ListAsync(string? search, int? statusId, CancellationToken cancellationToken)
    {
        var reader = await ReaderAsync(cancellationToken);
        if (reader == Reader.None)
        {
            return AppError.Forbidden;
        }

        var text = DisplayNames.Normalize(search);
        if (text.Length > DisplayNames.MaxLength)
        {
            return new List<ProductView>();
        }

        var found = await products.ListAsync(text, reader == Reader.Owner ? statusId : StatusIds.Usage.InUse, cancellationToken);
        return found.Select(p => View(p, reader)).ToList();
    }

    /// <summary>A Discontinued product can still be viewed (GBR-01), for example on an older invoice.</summary>
    public async Task<Result<ProductView>> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        var reader = await ReaderAsync(cancellationToken);
        if (reader == Reader.None)
        {
            return AppError.Forbidden;
        }

        return await products.GetForEditAsync(productId, cancellationToken) is { } product ? View(product, reader) : AppError.NotFound;
    }

    private async Task<Reader> ReaderAsync(CancellationToken cancellationToken)
    {
        if (currentUser.AccountId is not { } accountId || await permissions.GetAccessAsync(accountId, cancellationToken) is not { } access)
        {
            return Reader.None;
        }

        return access.IsCooperativeOwner ? Reader.Owner
            : access.SubjectType == SubjectType.Trader ? Reader.Trader
            : access.PermittedZonesFor(PermissionFunctions.SalesInvoices, PermissionAction.Create).IsEmpty ? Reader.None
            : Reader.Seller;
    }

    private ProductView View(Product p, Reader reader) => new(
        p.Id, p.Name, reader == Reader.Trader ? null : p.UnitPrice, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), RowVersionGuard.Encode(p.RowVersion));
}
