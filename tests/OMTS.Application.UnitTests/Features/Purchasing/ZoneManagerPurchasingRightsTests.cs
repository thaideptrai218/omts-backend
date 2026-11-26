using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using static OMTS.Application.Features.Authorization.PermissionFunctions;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>Owner decision of 25/11/2026: the Zone Manager does in Mua hang what the Owner does in every zone (shared warehouse), but only reads suppliers and never pays them.</summary>
public class ZoneManagerPurchasingRightsTests
{
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
        // The Zone Manager keeps the receipts, returns and price setup (PURCHASE_RECEIPTS) in every zone.
        var receipts = zoneManager.Single(p => p.FunctionCode == PurchaseReceipts);
        Assert.True(receipts.CanView && receipts.CanCreate && receipts.CanEdit && receipts.CanDelete);
        // Owner decision of 26/11/2026: the Accountant does the accounting side of purchasing in full, so it creates receipts too.
        Assert.True(accountant.Single(p => p.FunctionCode == PurchaseReceipts).CanCreate);
    }
}
