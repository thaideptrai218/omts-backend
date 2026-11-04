using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;

namespace OMTS.Api.Controllers;

/// <summary>
/// The fixed roles (BR-FARM-07) and the function catalog of their matrix; read only, Cooperative Owner only. Roles are
/// not created, edited, copied or discontinued in OMTS.
/// </summary>
[ApiController]
[Authorize]
[RequireCooperativeOwner]
public sealed class RolesController(RoleService roles) : ControllerBase
{
    /// <summary>The functions of the permission matrix (dashboard tiles included) and the actions each supports.</summary>
    [HttpGet("api/permission-functions")]
    [ProducesResponseType<IReadOnlyList<FunctionCatalogItem>>(StatusCodes.Status200OK)]
    public IActionResult Functions() => Ok(PermissionFunctions.All
        .Select(f => new FunctionCatalogItem(f.Code, f.Name, f.CooperativeWide, f.IsDashboardTile, f.SupportedActions.Select(a => a.ToString()).ToList()))
        .ToList());

    /// <summary>The two fixed roles, Zone Manager and Accountant, with their code and permissions.</summary>
    [HttpGet("api/roles")]
    [ProducesResponseType<IReadOnlyList<RoleView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await roles.ListAsync(cancellationToken));

    [HttpGet("api/roles/{id:guid}")]
    [ProducesResponseType<RoleView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await roles.GetAsync(id, cancellationToken)).ToActionResult(Ok);
}
