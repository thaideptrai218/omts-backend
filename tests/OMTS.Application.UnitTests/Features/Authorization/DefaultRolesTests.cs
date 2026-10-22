using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.UnitTests.Features.Authorization;

public class DefaultRolesTests
{
    [Fact]
    public void Default_roles_only_hold_supported_actions_of_catalog_functions()
    {
        foreach (var permission in DefaultRoles.All.SelectMany(r => r.Permissions))
        {
            var function = PermissionFunctions.Find(permission.FunctionCode);
            Assert.NotNull(function);
            Assert.All(DefaultRoles.ActionsOf(permission), action => Assert.True(function.Supports(action), $"{function.Code} {action}"));
            Assert.Contains(PermissionAction.View, DefaultRoles.ActionsOf(permission));
        }
    }

    [Fact]
    public void Every_catalog_function_supports_view()
    {
        Assert.All(PermissionFunctions.All, f => Assert.True(f.Supports(PermissionAction.View), f.Code));
        Assert.All(PermissionFunctions.All.Where(f => f.IsDashboardTile), f => Assert.Equal([PermissionAction.View], f.SupportedActions));
    }
}
