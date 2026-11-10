using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Application.Features.Traceability.Origin;

namespace OMTS.Api.Controllers;

/// <summary>
/// ORIGIN-01 – the public provenance page (SCR-ORIGIN-01) that the QR code of a sales invoice opens: anonymous, the same for a logged-in user (BR-ORIGIN-01), limited per client
/// IP (<c>RateLimits:ProvenancePagesPerMinute</c>, 429 TOO_MANY_REQUESTS) and never cached, so each opening shows the current data (BR-TRACE-05).
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingExtensions.ProvenancePages)]
public sealed class ProvenancePageController(ProvenancePageService pages) : ControllerBase
{
    /// <summary>
    /// The page in <c>lang</c> (<c>vi</c>, the default, or <c>en</c>). 200 with <c>status</c> Ok (lots listed), NoLots (MSG-ORIGIN-02) or Cancelled (cooperative block only,
    /// MSG-ORIGIN-04); 404 MSG-ORIGIN-03 for a QR code no invoice has.
    /// </summary>
    [HttpGet("api/public/provenance/{qrToken}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ProvenancePage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string qrToken, [FromQuery] string? lang, CancellationToken cancellationToken) =>
        (await pages.GetAsync(qrToken, lang, cancellationToken)).ToActionResult(Ok);
}
