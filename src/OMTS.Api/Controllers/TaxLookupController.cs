using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Features.TaxLookup;

namespace OMTS.Api.Controllers;

/// <summary>
/// The invoice form of a customer or a supplier: a company by its tax code, with its address split into the province and ward of the administrative units list. Only
/// whoever may create or edit customers or suppliers may ask; 30 a minute per account.
/// </summary>
[ApiController]
[Route("api/tax-lookup")]
[Authorize]
public sealed class TaxLookupController(TaxLookupService lookups) : ControllerBase
{
    /// <summary>400 MSG-GEN-01 on <c>Tax code</c> unless it is 10 digits or 10 digits, a hyphen and 3 digits; 404 when no company has it; 503 when the registry cannot be read.</summary>
    [HttpGet("{taxCode}")]
    [EnableRateLimiting(RateLimitingExtensions.TaxLookups)]
    [RequirePartyEditor]
    [ProducesResponseType<TaxLookupResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Lookup(string taxCode, CancellationToken cancellationToken) =>
        (await lookups.LookupAsync(taxCode, cancellationToken)).ToActionResult(Ok);
}
