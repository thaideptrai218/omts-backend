using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Api.Controllers;

/// <summary>
/// SALE-05 (SCR-SALE-04): the Zone Manager and the Owner sell at the port; the Accountant only views (§I.4.4). The filter needs the right in some zone; the service checks the zone of the
/// invoice and of every lot (BR-SALE-06, BR-SALE-08). The products come from <c>GET api/products</c>, the lots from <c>GET api/harvest-lots/for-sale</c> and a new profile from
/// <c>POST api/customers</c> (SALE-05.1).
/// </summary>
[ApiController]
[Route("api/sales-invoices")]
[Authorize]
public sealed class SalesInvoicesController(
    SalesInvoiceService invoices, SalesInvoiceReadService reads, SalesInvoiceSendingService sending, SalesInvoiceSearchService search, SalesInvoiceExportService exports,
    SalesInvoiceCorrectionService corrections, SalesInvoiceCancellationService cancellations, SalesInvoiceEditService edits, SalesInvoiceCodeService codes, PendingSaleService pendingSales)
    : ControllerBase
{
    /// <summary>
    /// SALES_INVOICES Edit (the Owner until the right is given to another role): changes a Completed invoice like a purchase receipt: the code, every line, the discount, the other charges and the note;
    /// the customer and the zone stay. 409 MSG-SALE-17 asks a question (<c>confirmOwedBack</c>); 409 MSG-SALE-20 (a sales return), MSG-SALE-21 (walk-in total raised), MSG-GEN-02 (the code is taken),
    /// MSG-GEN-09 (Cancelled or a lot no longer for sale), MSG06 (the row version is stale). The invoice is sent again.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceSent>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Edit(Guid id, EditSalesInvoiceRequest request, CancellationToken cancellationToken) =>
        (await edits.EditAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// SALE-07, SALES_INVOICES Edit: a price cut after a claim (<c>kind</c> PriceCutAfterClaim); a data entry error is changed with PUT of the invoice (400 with a hint). 409 MSG-SALE-17/19 asks a question,
    /// answered by the flag in <c>confirmationField</c>; 409 MSG-SALE-20 (a sales return), MSG-GEN-09 (Cancelled), MSG06 (the row version is stale). The invoice is sent again.
    /// </summary>
    [HttpPut("{id:guid}/correction")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceSent>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid id, CorrectSalesInvoiceRequest request, CancellationToken cancellationToken) =>
        (await corrections.CorrectAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// SALE-08, SALES_INVOICES Delete (the Owner until the right is given to another role): cancels the invoice with a reason; the stock goes back, the lots it closed reopen, the advance of its order
    /// is the order's again and a Completed order is Delivering. 409 MSG-SALE-23/19/17 asks a question, answered by the field in <c>confirmationField</c>; 409 MSG-SALE-20, MSG-GEN-09, MSG06.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Delete, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceSent>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelSalesInvoiceRequest request, CancellationToken cancellationToken) =>
        (await cancellations.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>SALE-09 steps 1-3: the invoices of the zones the caller may view, today by default, with the total of the Completed ones; 200 with MSG-GEN-10 when nothing matches; 400 MSG-GEN-01 for a wrong period.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceSearchResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] SalesInvoiceSearchQuery query, CancellationToken cancellationToken) =>
        (await search.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>SALE-09.2: the matches of the same filters and their total, every page, as an .xlsx file.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] SalesInvoiceSearchQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportListAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>SALE-09.2: one invoice as an .xlsx file; 403 MSG-GEN-08 outside the zones where the caller may export.</summary>
    [HttpGet("{id:guid}/print")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Print(Guid id, CancellationToken cancellationToken) =>
        (await exports.PrintAsync(id, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>
    /// Makes an invoice from an order (<c>salesOrderId</c>, required): its customer and zone are the order's, its lines name the order lines they deliver. 201 with the Completed invoice, the change,
    /// the order after the save (<c>order</c>, <c>orderCanFinish</c>: the web asks "Kết thúc đơn đặt hàng?" and calls POST sales-orders/{id}/finish) and MSG-SALE-06; with <c>payment.rest = VietQr</c> (all, or the rest after a cash part), 202 with the sale awaiting its transfer
    /// and its VietQR code (poll <c>GET pending/{code}</c>); 409 MSG-SALE-10/11/12 asks a question, answered by the flag named in <c>confirmationField</c> in the same request;
    /// 409 MSG-SALE-13 without a receiving bank account; 409 MSG-GEN-09 names a lot no longer for sale or a request no longer open.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceSaved>(StatusCodes.Status201Created)]
    [ProducesResponseType<PendingSaleView>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Create(CreateSalesInvoiceRequest request, CancellationToken cancellationToken) =>
        (await invoices.CreateAsync(request, cancellationToken)).ToActionResult(SavedOrAwaiting);

    /// <summary>The sales of the caller's zones still awaiting their VietQR transfer, oldest first.</summary>
    [HttpGet("pending")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<PendingSaleView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPending(CancellationToken cancellationToken) => Ok(await pendingSales.ListAwaitingAsync(cancellationToken));

    /// <summary>SALE-05 steps 7-8, polled every few seconds: Awaiting with the VietQR code, then Completed with the invoice and MSG-SALE-06 once the transfer arrived.</summary>
    [HttpGet("pending/{code}")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<PendingSaleView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPending(string code, CancellationToken cancellationToken) =>
        (await pendingSales.GetAsync(code, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// SALE-05.0.E4 step 2: Cash, part in cash with Pay later, or Pay later instead of the transfer; 201 with the invoice under the same code, the questions as for POST;
    /// 409 MSG-GEN-09 when the transfer arrived meanwhile (<c>status</c>, <c>invoiceId</c>).
    /// </summary>
    [HttpPost("pending/{code}/change-payment")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> ChangePendingPayment(string code, ChangePendingSalePaymentRequest request, CancellationToken cancellationToken) =>
        (await invoices.ChangePaymentAsync(code, request, cancellationToken)).ToActionResult(SavedOrAwaiting);

    /// <summary>SALE-05.0.E4 step 3: 409 MSG-SALE-14 asks first (<c>confirmed</c>); 200 Stopped, nothing saved but the code in the audit log.</summary>
    [HttpPost("pending/{code}/stop")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<PendingSaleView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StopPending(string code, StopPendingSaleRequest request, CancellationToken cancellationToken) =>
        (await pendingSales.StopAsync(code, request, cancellationToken)).ToActionResult(Ok);

    private ObjectResult SavedOrAwaiting(SalesInvoiceCreated created) => created.Saved is { } saved
        ? StatusCode(StatusCodes.Status201Created, saved)
        : StatusCode(StatusCodes.Status202Accepted, created.Pending);

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await reads.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// TRACE-01.0 steps 1-2 before the end of the sale: each lot with its origin state, and for a Not published lot what it misses (MSG-TRACE-01); <c>getsQrCode</c> says whether a Yes
    /// to MSG-SALE-07 now gives the QR code. Changes nothing.
    /// </summary>
    [HttpGet("{id:guid}/publish-check")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<InvoicePublishCheck>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PublishCheck(Guid id, CancellationToken cancellationToken) =>
        (await reads.PublishCheckAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// SALE-05 steps 9-11, once per invoice after the save: 409 MSG-SALE-07 asks the QR question (<c>confirmationField</c> = <c>wantsQrCode</c>); with a Yes, TRACE-01 publishes the lots
    /// that are Not published and have all their origin information (the others are listed with MSG-TRACE-01), the QR code is created when a lot is Published, and the invoice is sent
    /// (SALE-06). 200 with the delivery status, MSG-SALE-16 when nothing could be sent; called again, 200 with the current result.
    /// </summary>
    [HttpPost("{id:guid}/finish")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceSent>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Finish(Guid id, FinishSalesInvoiceRequest request, CancellationToken cancellationToken) =>
        (await sending.FinishAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>SALE-06.1: sends the invoice again at its latest values; 409 MSG-GEN-09 while the latest sending is Sending or for a Cancelled invoice.</summary>
    [HttpPost("{id:guid}/resend")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceSent>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resend(Guid id, CancellationToken cancellationToken) =>
        (await sending.ResendAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>The code the next invoice would get (<c>{ code }</c>), without spending its number.</summary>
    [HttpGet("next-code")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    public async Task<IActionResult> NextCode(CancellationToken cancellationToken) => Ok(new { code = await codes.PeekAsync(cancellationToken) });

    /// <summary>
    /// Deprecated: an invoice is made from an order, not from a purchase request, so this answers an empty list (the route stays so an older web does not get 404). The request becomes an order with
    /// <c>POST api/sales-orders</c> and <c>purchaseRequestId</c>.
    /// </summary>
    [HttpGet("open-purchase-requests")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<OpenPurchaseRequestView>>(StatusCodes.Status200OK)]
    public IActionResult OpenPurchaseRequests(Guid customerId) => Ok(Array.Empty<OpenPurchaseRequestView>());
}
