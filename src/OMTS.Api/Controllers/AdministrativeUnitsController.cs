using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces.Addresses;
using OMTS.Application.Features.AdministrativeUnits;

namespace OMTS.Api.Controllers;

/// <summary>
/// The provinces and wards of Vietnam in force since 1/7/2025 (two levels, no district) for the address pickers of the customer and supplier forms. Read from an
/// outside list and kept 24 hours; any signed-in staff account may read them. 503 when the list cannot be read.
/// </summary>
[ApiController]
[Route("api/administrative-units")]
[Authorize]
[RequireStaff]
public sealed class AdministrativeUnitsController(AdministrativeUnitService units) : ControllerBase
{
    [HttpGet("provinces")]
    [ProducesResponseType<IReadOnlyList<ProvinceUnit>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Provinces(CancellationToken cancellationToken) =>
        (await units.ProvincesAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>The wards of a province, 404 for a code that is not one.</summary>
    [HttpGet("provinces/{code:int}/wards")]
    [ProducesResponseType<IReadOnlyList<WardUnit>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Wards(int code, CancellationToken cancellationToken) =>
        (await units.WardsAsync(code, cancellationToken)).ToActionResult(Ok);
}
