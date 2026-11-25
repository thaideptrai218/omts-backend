using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;
using static OMTS.Application.Features.Authorization.PermissionFunctions;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>Owner decision of 25/11/2026: a Zone Manager sees and writes only the receipts of its zones; the Owner and the Accountant see every receipt.</summary>
public class PurchaseReceiptScopeTests
{
    private static readonly Guid ZoneA = Guid.NewGuid();
    private static readonly Guid ZoneB = Guid.NewGuid();

    private static ReceiptScope Zones(params Guid[] zones) => new(false, zones.ToHashSet());

    [Fact]
    public void A_zone_scope_sees_its_zones_and_never_a_receipt_with_no_zone()
    {
        var scope = Zones(ZoneA);

        Assert.True(scope.Sees(ZoneA));
        Assert.False(scope.Sees(ZoneB));
        Assert.False(scope.Sees(null));
        Assert.True(ReceiptScope.Everything.Sees(null));
        Assert.True(ReceiptScope.Everything.Sees(ZoneB));
    }

    [Fact]
    public void A_list_is_limited_to_the_zones_asked_for_inside_the_scope()
    {
        Assert.Equal([ZoneA], Zones(ZoneA).Limit(null));
        Assert.Equal([ZoneA], Zones(ZoneA, ZoneB).Limit([ZoneA]));
        // A zone asked for outside the scope gives nothing, never the whole scope.
        Assert.Empty(Zones(ZoneA).Limit([ZoneB])!);
        Assert.Null(ReceiptScope.Everything.Limit(null));
        Assert.Equal([ZoneB], ReceiptScope.Everything.Limit([ZoneB]));
    }

    [Fact]
    public void The_zone_manager_reads_suppliers_only_and_the_accountant_alone_pays_them()
    {
        var zoneManager = FixedRoles.ZoneManager.GrantedPermissions;
        var accountant = FixedRoles.Accountant.GrantedPermissions;

        var suppliers = zoneManager.Single(p => p.FunctionCode == Suppliers);
        Assert.True(suppliers.CanView);
        Assert.False(suppliers.CanCreate || suppliers.CanEdit || suppliers.CanDelete);
        Assert.DoesNotContain(zoneManager, p => p.FunctionCode == SupplierPayments);
        Assert.True(accountant.Single(p => p.FunctionCode == SupplierPayments).CanCreate);
        // The Zone Manager keeps the receipts, returns and price setup (PURCHASE_RECEIPTS) in its zones.
        var receipts = zoneManager.Single(p => p.FunctionCode == PurchaseReceipts);
        Assert.True(receipts.CanView && receipts.CanCreate && receipts.CanEdit && receipts.CanDelete);
        Assert.False(accountant.Single(p => p.FunctionCode == PurchaseReceipts).CanCreate);
    }
}
