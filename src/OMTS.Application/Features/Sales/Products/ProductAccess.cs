using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Products;

public enum ProductReader
{
    None,
    Owner,

    /// <summary>Records sales invoices: a Zone Manager.</summary>
    Seller,

    /// <summary>Only looks: views invoices, disposals or harvest lots, like the Accountant.</summary>
    Viewer,

    /// <summary>A trader: names only, no price and no stock.</summary>
    Trader,

    /// <summary>Records harvests (HARVESTS Create or Edit) but holds no other product right: the plain list and detail of products In Use to pick the products of a lot, with no price and no stock.</summary>
    Picker,
}

/// <summary>
/// Who reads the products: the Owner reads all of them (SALE-11); who sells or sends requests lists those offered, the Zone Manager with their prices (SALE-05 step 1), the trader with no price (SALE-01 step 1,
/// BR-SALE-01); the catalogue screen (stock, movements, lots, price history) is open to the Owner, the Zone Manager and whoever views invoices, disposals or harvest lots, like the Accountant. Writes are the Owner's (API filter).
/// </summary>
public sealed class ProductAccess(ICurrentUser currentUser, PermissionService permissions)
{
    public async Task<ProductReader> ReaderAsync(CancellationToken cancellationToken)
    {
        return currentUser.AccountId is { } accountId && await permissions.GetAccessAsync(accountId, cancellationToken) is { } access ? ReaderOf(access) : ProductReader.None;
    }

    /// <summary>What a caller with these rights reads of the products.</summary>
    public static ProductReader ReaderOf(AccountAccess access)
    {
        if (access.IsCooperativeOwner)
        {
            return ProductReader.Owner;
        }

        if (access.SubjectType == SubjectType.Trader)
        {
            return ProductReader.Trader;
        }

        if (!access.PermittedZonesFor(PermissionFunctions.SalesInvoices, PermissionAction.Create).IsEmpty)
        {
            return ProductReader.Seller;
        }

        var viewing = new[] { PermissionFunctions.SalesInvoices, PermissionFunctions.ProductDisposals, PermissionFunctions.HarvestLots };
        if (viewing.Any(f => !access.PermittedZonesFor(f, PermissionAction.View).IsEmpty))
        {
            return ProductReader.Viewer;
        }

        // A harvest records the products of its lot, so whoever records or corrects harvests can pick them, whatever else the role holds.
        return new[] { PermissionAction.Create, PermissionAction.Edit }.Any(a => !access.PermittedZonesFor(PermissionFunctions.Harvests, a).IsEmpty) ? ProductReader.Picker : ProductReader.None;
    }

    /// <summary>
    /// The zones whose lots the caller sees in the movements and lots tabs: null for the Owner and for a caller granted every zone, otherwise the zones of its pairs. Product stock is cooperative-wide,
    /// but the sales, customers and disposals behind a row are those of the zone of its harvest lot.
    /// </summary>
    public async Task<IReadOnlySet<Guid>?> ZoneScopeAsync(CancellationToken cancellationToken)
    {
        if (currentUser.AccountId is not { } accountId || await permissions.GetAccessAsync(accountId, cancellationToken) is not { } access)
        {
            return new HashSet<Guid>();
        }

        if (access.IsCooperativeOwner)
        {
            return null;
        }

        var zones = new HashSet<Guid>();
        foreach (var function in new[] { PermissionFunctions.SalesInvoices, PermissionFunctions.ProductDisposals, PermissionFunctions.HarvestLots })
        {
            var permitted = access.PermittedZonesFor(function, PermissionAction.View);
            if (permitted.AllZones)
            {
                return null;
            }

            zones.UnionWith(permitted.ZoneIds);
        }

        return zones;
    }

    /// <summary>Whether the reader sees the catalogue screen: not a trader, not a caller with no right.</summary>
    public static bool IsCatalogueReader(ProductReader reader) => reader is ProductReader.Owner or ProductReader.Seller or ProductReader.Viewer;
}
