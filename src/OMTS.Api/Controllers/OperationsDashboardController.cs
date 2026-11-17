using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Dashboard;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-06 – the operations dashboard; Cooperative Owner only (SRS 27/10), anyone else gets 403 MSG-GEN-08. Read only: every figure is worked out from the records when asked (BR-FARM-10).
/// The gate is the Owner account type. The <c>DASHBOARD_*</c> functions of <c>PermissionFunctions</c> only tell the client which tiles to show in <c>/api/me/permissions</c>; no role can hold them.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
[RequireCooperativeOwner]
public sealed class OperationsDashboardController(OperationsDashboardService dashboard) : ControllerBase
{
    /// <summary>
    /// Every tile now, with <c>computedAt</c>. <c>from</c>/<c>to</c> (Vietnam dates, both included; today when neither is given, only <c>to</c> is that one day, only <c>from</c> runs to today,
    /// at most 5 years) apply to the daily revenue only. <c>zoneId</c> applies to the revenue (by the zone of the invoice), the open harvest lots and the water alerts; receivables and pending
    /// tasks are always of the whole cooperative. A period that cannot be read, or a zone that does not exist, is 400 MSG-GEN-01 naming the field.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<OperationsDashboard>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(DateOnly? from, DateOnly? to, Guid? zoneId, CancellationToken cancellationToken) =>
        (await dashboard.GetAsync(from, to, zoneId, cancellationToken)).ToActionResult(Ok);
}
