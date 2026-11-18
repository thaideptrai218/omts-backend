using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Reports.EndOfDay;

namespace OMTS.Api.Controllers;

/// <summary>
/// End-of-day report (KiotViet "Cuối ngày"): the Cooperative Owner and the Accountant (SALES_REPORTS, decision D5 of 19/11/2026), nobody else (403 MSG-GEN-08). Read only: the figures are worked
/// out each time and nothing is stored.
/// </summary>
[ApiController]
[Route("api/reports/end-of-day")]
[Authorize]
public sealed class EndOfDayReportController(EndOfDayService report, EndOfDayExportService exports) : ControllerBase
{
    /// <summary>
    /// The sales (invoices and returns), the money (cash book lines by fund) and the goods (kg per product, lots weighed) of one business day (<c>date</c>, default today), in the zones chosen
    /// (<c>zoneIds</c>, repeated; sales and lots only), of one <c>method</c> Cash, Bank or EWallet (money only) and one <c>createdBy</c> account. MSG-GEN-10 in <c>message</c> when the day has nothing;
    /// 400 MSG-GEN-01 naming the field; 403 MSG-GEN-08 for a zone the caller cannot access.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<EndOfDayReport>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report([FromQuery] EndOfDayQuery query, CancellationToken cancellationToken) =>
        (await report.ReportAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The report as a printable file BaoCaoCuoiNgay_{yyyyMMdd}: <c>format</c> xlsx (default) or pdf, any other value is 400 MSG-GEN-01 naming "format". Needs the Export right.
    /// </summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] EndOfDayQuery query, [FromQuery] string? format, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, format, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
