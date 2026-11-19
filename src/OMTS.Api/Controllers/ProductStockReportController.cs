using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Reports.ProductStock;

namespace OMTS.Api.Controllers;

/// <summary>
/// Product stock report (KiotViet "Hàng hóa – Xuất nhập tồn"): the Cooperative Owner and the Accountant (SALES_REPORTS, decision D5 of 19/11/2026), nobody else (403 MSG-GEN-08). The stock ledger
/// has no zone, so a user limited to some zones is refused. Read only: the figures are worked out each time and nothing is stored.
/// </summary>
[ApiController]
[Route("api/reports/product-stock")]
[Authorize]
public sealed class ProductStockReportController(ProductStockService report, ProductStockExportService exports) : ControllerBase
{
    /// <summary>
    /// The kg on hand at the start, received, sold, disposed, adjusted and on hand at the end of each product (<c>groupBy</c> product, default) or product category, in the period (<c>from</c>/<c>to</c>,
    /// default the current month), for the products and categories chosen (<c>productIds</c>, <c>categoryIds</c>, repeated). MSG-GEN-10 in <c>message</c> when no product has stock rows; 400 MSG-GEN-01
    /// naming the field for a start after the end, an unknown grouping or an unknown id.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<ProductStockReport>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report([FromQuery] ProductStockQuery query, CancellationToken cancellationToken) =>
        (await report.ReportAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The figures shown, with the period and the choices, as product-stock-{groupBy}-{from}-{to}.xlsx. Needs the Export right.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] ProductStockQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
