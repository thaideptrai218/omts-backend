using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;

namespace OMTS.Api.Controllers;

/// <summary>ACS-10 roles and the function catalog they are built from; Cooperative Owner only. No delete.</summary>
[ApiController]
[Authorize]
[RequireCooperativeOwner]
public sealed class RolesController(RoleService roles) : ControllerBase
{
    /// <summary>The grantable functions (dashboard tiles included) and the actions each supports.</summary>
    [HttpGet("api/permission-functions")]
    [ProducesResponseType<IReadOnlyList<FunctionCatalogItem>>(StatusCodes.Status200OK)]
    public IActionResult Functions() => Ok(PermissionFunctions.All
        .Select(f => new FunctionCatalogItem(f.Code, f.Name, f.CooperativeWide, f.IsDashboardTile, f.SupportedActions.Select(a => a.ToString()).ToList()))
        .ToList());

    [HttpGet("api/roles")]
    [ProducesResponseType<IReadOnlyList<RoleView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(int? statusId, CancellationToken cancellationToken) =>
        Ok(await roles.ListAsync(statusId, cancellationToken));

    [HttpGet("api/roles/{id:guid}")]
    [ProducesResponseType<RoleView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await roles.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost("api/roles")]
    [ProducesResponseType<RoleView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveRoleRequest request, CancellationToken cancellationToken) =>
        (await roles.CreateAsync(request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>Applies at once to every holder; ask for MSG09 confirmation with <c>GET .../impact</c> first.</summary>
    [HttpPut("api/roles/{id:guid}")]
    [ProducesResponseType<RoleView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveRoleRequest request, CancellationToken cancellationToken) =>
        (await roles.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("api/roles/{id:guid}/copy")]
    [ProducesResponseType<RoleView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Copy(Guid id, CopyRoleRequest request, CancellationToken cancellationToken) =>
        (await roles.CopyAsync(id, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    [HttpPost("api/roles/{id:guid}/discontinue")]
    [ProducesResponseType<RoleView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid id, CancellationToken cancellationToken) =>
        (await roles.DiscontinueAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost("api/roles/{id:guid}/reactivate")]
    [ProducesResponseType<RoleView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken) =>
        (await roles.ReactivateAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/roles/{id:guid}/impact")]
    [ProducesResponseType<RoleImpact>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Impact(Guid id, CancellationToken cancellationToken) =>
        (await roles.ImpactAsync(id, cancellationToken)).ToActionResult(Ok);
}
