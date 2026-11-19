using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Reports.Financial;

namespace OMTS.Api.Controllers;

/// <summary>
/// Financial report (KiotViet "Tài chính", the business result): the Cooperative Owner and the Accountant (SALES_REPORTS, decision D5 of 19/11/2026; the plan first said Owner only), nobody else
/// (403 MSG-GEN-08). The figures are cooperative-wide, so a user limited to some zones is refused. Read only: worked out each time and never stored.
/// </summary>
[ApiController]
[Route("api/reports/financial")]
[Authorize]
public sealed class FinancialReportController(FinancialReportService report, FinancialReportExportService exports) : ControllerBase
{
    /// <summary>
    /// The statement of the period (<c>from</c>/<c>to</c>, default 1 January to today, at most 5 years) with one column per <c>unit</c> month (default), quarter or year and the lines
    /// revenue, deductions, net revenue, costs, other income and profit. MSG-GEN-10 in <c>message</c> when every figure is 0; 400 MSG-GEN-01 naming the field.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<FinancialReport>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report([FromQuery] FinancialReportQuery query, CancellationToken cancellationToken) =>
        (await report.ReportAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The statement shown as financial-{unit}-{from}-{to}.xlsx. Needs the Export right.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] FinancialReportQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
