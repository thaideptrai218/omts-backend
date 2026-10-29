using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Farming;

namespace OMTS.Api.Controllers;

[ApiController]
[Authorize]
[RequireCooperativeOwner]
[Route("api/me/zalo-location")]
public sealed class ZaloLocationController(ZaloLocationService locations, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Resolve a single-use Zalo location token; saving a zone/raft remains a separate explicit action.</summary>
    [HttpPost]
    [ProducesResponseType<LocationCoordinates>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resolve(ZaloLocationRequest request, CancellationToken cancellationToken) =>
        (await locations.ResolveAsync(currentUser.AccountId!.Value, request, cancellationToken)).ToActionResult(Ok);
}
