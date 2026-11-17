using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Reports.PriceTrends;

namespace OMTS.Api.Controllers;

/// <summary>
/// REPORT-02 – analyze price trends (SCR-REPORT-02): the Cooperative Owner and the Accountant (§I.4.4; SALES_REPORTS), nobody else (403 MSG-GEN-08). A pair in any zone opens the route; the
/// service limits the zones to those of the caller's pairs (BR-FARM-08). Read only: the figures are worked out each time and nothing is stored.
/// </summary>
[ApiController]
[Route("api/reports/price-trends")]
[Authorize]
public sealed class PriceTrendsReportController(PriceTrendsService report, PriceTrendsExportService exports) : ControllerBase
{
    /// <summary>
    /// The sold kg, the average (weighted by kg), lowest and highest unit price of each product (<c>productIds</c>, repeated, default all) per <c>unit</c> day (default), week or month in the
    /// period (<c>from</c>/<c>to</c>, default the current month), in the zones chosen (<c>zoneIds</c>, repeated). MSG-GEN-10 in <c>message</c> when no line matches; 400 MSG-GEN-01 naming the
    /// field for a start after the end, an unknown unit or an unknown id; 403 MSG-GEN-08 for a zone the caller cannot access.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PriceTrendsReport>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report([FromQuery] PriceTrendsQuery query, CancellationToken cancellationToken) =>
        (await report.ReportAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>REPORT-02.2: the invoice lines counted for one product (<c>productId</c>) in one period (<c>periodKey</c>, the <c>key</c> of a period of the report) with the same selection.</summary>
    [HttpGet("lines")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PriceTrendLineList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Lines([FromQuery] PriceTrendsQuery query, CancellationToken cancellationToken) =>
        (await report.LinesAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>REPORT-02.3: the figures shown, with the period and the choices, as price-trends-{unit}-{from}-{to}.xlsx. Needs the Export right.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] PriceTrendsQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
