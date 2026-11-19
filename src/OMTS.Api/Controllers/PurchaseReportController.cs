using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Reports.Purchases;

namespace OMTS.Api.Controllers;

/// <summary>
/// Purchases report (KiotViet "Nhà cung cấp – Hàng nhập theo NCC", no VAT): the Cooperative Owner and the Accountant (SUPPLIER_PAYMENTS, decision D5 of 19/11/2026), nobody else (403 MSG-GEN-08).
/// Purchases have no zone. Read only: the figures are worked out each time and nothing is stored.
/// </summary>
[ApiController]
[Route("api/reports/purchases")]
[Authorize]
public sealed class PurchaseReportController(PurchaseReportService report, PurchaseReportExportService exports) : ControllerBase
{
    /// <summary>
    /// The Completed receipts (by receipt date) and the Recorded returns (by return date) of the period (<c>from</c>/<c>to</c>, default the current month), <c>groupBy</c> supplier (default) or item, for
    /// the suppliers chosen (<c>supplierIds</c>, repeated) and the <c>kind</c> Seed, Supply or all (default). MSG-GEN-10 in <c>message</c> when there is none; 400 MSG-GEN-01 naming the field.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReport>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report([FromQuery] PurchaseReportQuery query, CancellationToken cancellationToken) =>
        (await report.ReportAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The receipts and returns of one group (<c>groupKey</c> is the <c>key</c> of a group of the report) with the same selection, newest first.</summary>
    [HttpGet("lines")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseDocumentList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Lines([FromQuery] PurchaseReportQuery query, CancellationToken cancellationToken) =>
        (await report.DocumentsAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The figures shown, with the period and the choices, as purchases-{groupBy}-{from}-{to}.xlsx. Needs the Export right.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] PurchaseReportQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
