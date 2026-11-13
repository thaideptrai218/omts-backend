using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Traceability.Origin;
using OMTS.Application.Features.Traceability.Origin.Dtos;

namespace OMTS.Api.Controllers.Traceability;

/// <summary>The public provenance page a QR code opens (BR-ORIGIN-01): anonymous, read only, never cached so a withdrawal shows at once.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/origin")]
public sealed class PublicOriginController(PublicOriginService origins) : ControllerBase
{
    /// <summary>The Published and Withdrawn lots of the invoice with this QR token; 404 when the token is unknown, the invoice cancelled or no lot was published.</summary>
    [HttpGet("{token}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<PublicOriginView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string token, CancellationToken cancellationToken) =>
        (await origins.GetAsync(token, cancellationToken)).ToActionResult(Ok);
}
