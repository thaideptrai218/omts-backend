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
    /// One concern of the report: <c>concern</c> sales (default), money, goods or summary; only the block of that concern is in the answer. The period is <c>from</c> to <c>to</c>, Vietnam local
    /// time <c>yyyy-MM-ddTHH:mm</c>, both included (<c>to</c> up to the end of its minute); today 00:00 to 23:59 when none is given, and <c>date</c> (yyyy-MM-dd) alone is the whole of that day.
    /// A period that ends before it starts or is longer than 366 days is 400 MSG-GEN-01 naming "to". <c>zoneIds</c> (repeated) limits the sales, the goods and the lots; money has no zone and is
    /// empty for a user limited to some zones. The other lists are repeated and belong to one concern (the others leave them alone): sales <c>customerIds</c>, <c>receivedBy</c> (who took the
    /// order), <c>createdBy</c>, <c>paymentMethods</c> (Cash, VietQrInvoice, OtherTransfer, EWallet: documents with a payment by one of them), <c>channels</c> (App, Phone, AtPort; an invoice or a
    /// return by its order), <c>paymentTerms</c> (PayNow, PayLater; invoices and the returns of such invoices, no orders); money <c>funds</c> (Cash, Bank, EWallet), <c>sources</c>
    /// (CustomerPayment, CustomerPaidBack, SupplierPayment, SupplierPaidBack, CashVoucher), <c>typeIds</c>, <c>partyKinds</c> (Customer, Supplier, Employee, Other), <c>createdBy</c>; goods
    /// <c>productIds</c>, <c>categoryIds</c> (a category includes what is below it), <c>customerIds</c>, <c>createdBy</c>; summary <c>createdBy</c>. A value that is not one of the names, or an
    /// id that does not exist, is 400 MSG-GEN-01 naming the field; 403 MSG-GEN-08 for a zone the caller cannot access. <c>filters</c> in the answer names what was chosen. MSG-GEN-10 in
    /// <c>message</c> when the period has nothing for the concern.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<EndOfDayReport>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report([FromQuery] EndOfDayQuery query, CancellationToken cancellationToken) =>
        (await report.ReportAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The report as a printable file BaoCaoCuoiNgay_{BanHang|ThuChi|HangHoa|TongHop}_{yyyyMMdd}[-{yyyyMMdd}] with the same selection as <c>GET</c>: <c>format</c> xlsx (default) or pdf,
    /// <c>orientation</c> portrait (default) or landscape (A4), any other value is 400 MSG-GEN-01 naming "format" or "orientation". <c>expanded=true</c> (goods) prints the invoices and returns
    /// under each product. Needs the Export right.
    /// </summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SalesReports, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export(
        [FromQuery] EndOfDayQuery query, [FromQuery] string? format, [FromQuery] string? orientation, [FromQuery] bool expanded, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, format, orientation, expanded, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
