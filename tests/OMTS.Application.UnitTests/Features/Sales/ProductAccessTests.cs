using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Products;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>Who reads the products: the roles of the matrix keep what they had, and a role that only records harvests can still pick the products of a lot.</summary>
public sealed class ProductAccessTests
{
    private static AccountAccess Staff(params ZoneGrant[] grants) => new(Guid.NewGuid(), SubjectType.Staff, false, true, grants);

    private static ZoneGrant Custom(Guid? zoneId, string function, params PermissionAction[] actions) =>
        new(zoneId, Guid.NewGuid(), "CUSTOM", "Harvest only", [GrantedPermission.Of(function, actions)]);

    private static ZoneGrant Pair(FixedRole role) => new(null, Guid.NewGuid(), role.Code, role.Name, role.GrantedPermissions);

    [Theory]
    [InlineData(PermissionAction.Create)]
    [InlineData(PermissionAction.Edit)]
    public void A_role_with_only_harvest_create_or_edit_is_a_picker(PermissionAction action) =>
        Assert.Equal(ProductReader.Picker, ProductAccess.ReaderOf(Staff(Custom(Guid.NewGuid(), PermissionFunctions.Harvests, action))));

    [Fact]
    public void A_role_with_only_harvest_view_or_unrelated_rights_reads_no_product()
    {
        Assert.Equal(ProductReader.None, ProductAccess.ReaderOf(Staff(Custom(Guid.NewGuid(), PermissionFunctions.Harvests, PermissionAction.View))));
        Assert.Equal(ProductReader.None, ProductAccess.ReaderOf(Staff(Custom(Guid.NewGuid(), PermissionFunctions.WaterMeasurements, PermissionAction.Create))));
    }

    [Fact]
    public void The_fixed_roles_keep_their_readers()
    {
        Assert.Equal(ProductReader.Seller, ProductAccess.ReaderOf(Staff(Pair(FixedRoles.ZoneManager))));
        Assert.Equal(ProductReader.Viewer, ProductAccess.ReaderOf(Staff(Pair(FixedRoles.Accountant))));
        Assert.Equal(ProductReader.Owner, ProductAccess.ReaderOf(new AccountAccess(Guid.NewGuid(), SubjectType.CooperativeOwner, false, true, [])));
    }
}
