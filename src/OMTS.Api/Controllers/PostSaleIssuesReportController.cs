using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Reports.PostSaleIssues;

namespace OMTS.Api.Controllers;

/// <summary>
/// REPORT-03 – analyze post-sale issues (SCR-REPORT-03): the Cooperative Owner and the Accountant (§I.4.4; SALES_REPORTS), nobody else (403 MSG-GEN-08). A pair in any zone opens the route;
/// the service limits the zones to those of the caller's pairs (BR-FARM-08). Read only: the figures are worked out each time and nothing is stored.
/// </summary>
[ApiController]
[Route("api/reports/post-sale-issues")]
[Authorize]
public sealed class PostSaleIssuesReportController(PostSaleIssuesService report, PostSaleIssuesExportService exports) : ControllerBase
{
    /// <summary>
    /// The claims, sales returns, price cuts and cancelled invoices dated in the period (<c>from</c>/<c>to</c>, default the current month; each by its own date), <c>groupBy</c> customer
    /// (default), zone or lot, and the zones and customers chosen (<c>zoneIds</c>, <c>customerIds</c>, repeated). MSG-GEN-10 in <c>message</c> when there is none; 400 MSG-GEN-01 naming the
    /// field for a start after the end, an unknown grouping or an unknown id; 403 MSG-GEN-08 for a zone the caller cannot access.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PostSaleIssuesReport>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report([FromQuery] PostSaleIssuesQuery query, CancellationToken cancellationToken) =>
        (await report.ReportAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>REPORT-03.2: the events of one group (<c>groupKey</c> is the <c>key</c> of a group of the report) with the same selection, newest first.</summary>
    [HttpGet("items")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PostSaleItemList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Items([FromQuery] PostSaleIssuesQuery query, CancellationToken cancellationToken) =>
        (await report.ItemsAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>REPORT-03.3: the figures shown, with the period and the choices, as post-sale-issues-{groupBy}-{from}-{to}.xlsx. Needs the Export right.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] PostSaleIssuesQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
