using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>ACS-09 staff accounts and FARM-05 zone–role grants; Cooperative Owner only.</summary>
[ApiController]
[Route("api/accounts")]
[Authorize]
[RequireCooperativeOwner]
public sealed class AccountsController(StaffAccountService staffAccounts, AccessGrantService grants) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<Paged<StaffAccountView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(string? search, int? statusId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await staffAccounts.SearchAsync(search, statusId, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<StaffAccountView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await staffAccounts.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Creates a staff account with at least one zone–role row; the temporary password is returned once.</summary>
    [HttpPost]
    [ProducesResponseType<StaffAccountWithPassword>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateStaffAccountRequest request, CancellationToken cancellationToken) =>
        (await staffAccounts.CreateAsync(request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    [HttpPut("{id:guid}")]
    [ProducesResponseType<StaffAccountView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, UpdateStaffAccountRequest request, CancellationToken cancellationToken) =>
        (await staffAccounts.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/lock")]
    [ProducesResponseType<StaffAccountView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Lock(Guid id, ReasonRequest request, CancellationToken cancellationToken) =>
        (await staffAccounts.LockAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/unlock")]
    [ProducesResponseType<StaffAccountView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Unlock(Guid id, ReasonRequest request, CancellationToken cancellationToken) =>
        (await staffAccounts.UnlockAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Issues a new temporary password (returned once, never sent) and ends every session.</summary>
    [HttpPost("{id:guid}/reset-password")]
    [ProducesResponseType<StaffAccountWithPassword>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetPassword(Guid id, CancellationToken cancellationToken) =>
        (await staffAccounts.ResetPasswordAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpGet("{id:guid}/grants")]
    [ProducesResponseType<IReadOnlyList<GrantRow>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListGrants(Guid id, CancellationToken cancellationToken) =>
        (await grants.ListAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/grants")]
    [ProducesResponseType<GrantRow>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Grant(Guid id, GrantDraft request, CancellationToken cancellationToken) =>
        (await grants.GrantAsync(id, request, cancellationToken)).ToActionResult(g => StatusCode(StatusCodes.Status201Created, g));

    [HttpPost("{id:guid}/grants/{grantId:guid}/revoke")]
    [ProducesResponseType<GrantRow>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(Guid id, Guid grantId, ReasonRequest request, CancellationToken cancellationToken) =>
        (await grants.RevokeAsync(id, grantId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>What draft zone–role rows would allow, by the same rules that enforce permissions.</summary>
    [HttpPost("permission-preview")]
    [ProducesResponseType<IReadOnlyList<FunctionAccess>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(PermissionPreviewRequest request, CancellationToken cancellationToken) =>
        (await grants.PreviewAsync(request, cancellationToken)).ToActionResult(Ok);
}
