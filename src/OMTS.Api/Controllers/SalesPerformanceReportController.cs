using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Reports.SalesPerformance;

namespace OMTS.Api.Controllers;

/// <summary>
/// REPORT-01 – analyze sales performance (SCR-REPORT-01): the Cooperative Owner and the Accountant (§I.4.4; SALES_REPORTS), nobody else (403 MSG-GEN-08). A pair in any zone opens the
/// route; the service limits the zones to those of the caller's pairs (BR-FARM-08). Read only: the figures are worked out each time and nothing is stored.
/// </summary>
[ApiController]
[Route("api/reports/sales-performance")]
[Authorize]
public sealed class SalesPerformanceReportController(SalesPerformanceService report, SalesPerformanceExportService exports) : ControllerBase
{
    /// <summary>
    /// The figures of the groups and the total for the period (<c>from</c>/<c>to</c>, default the current month), <c>groupBy</c> customer (default), product, zone, day, week or month, and
    /// the zones, customers and products chosen (<c>zoneIds</c>, <c>customerIds</c>, <c>productIds</c>, each repeated). MSG-GEN-10 in <c>message</c> when no invoice matches; 400 MSG-GEN-01
    /// naming the field for a start after the end, an unknown grouping or an unknown id; 403 MSG-GEN-08 for a zone the caller cannot access.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesPerformanceReport>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report([FromQuery] SalesPerformanceQuery query, CancellationToken cancellationToken) =>
        (await report.ReportAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>REPORT-01.3: the invoices of one group (<c>groupKey</c> is the <c>key</c> of a group of the report) with the same selection, newest first.</summary>
    [HttpGet("invoices")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesPerformanceInvoiceList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Invoices([FromQuery] SalesPerformanceQuery query, CancellationToken cancellationToken) =>
        (await report.InvoicesAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>REPORT-01.4: the figures shown, with the period and the choices, as sales-performance-{groupBy}-{from}-{to}.xlsx. Needs the Export right.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] SalesPerformanceQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
