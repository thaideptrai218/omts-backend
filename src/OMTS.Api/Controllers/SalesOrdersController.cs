using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.Orders;

namespace OMTS.Api.Controllers;

/// <summary>
/// The orders of goods (Đặt hàng, DH000001) that every sales invoice is made from. An order has no zone, so the rights are those of the role: the Owner does everything; a Zone Manager saves and changes the orders
/// of the buyers at the port and finishes them but never cancels one; the Accountant reads and exports. An order made from a trader's purchase request is the Owner's.
/// </summary>
[ApiController]
[Route("api/sales-orders")]
[Authorize]
public sealed class SalesOrdersController(SalesOrderService orders, SalesOrderReadService reads, SalesOrderAdvanceService advances, SalesInvoiceDraftService invoiceDrafts) : ControllerBase
{
    /// <summary>The orders the caller may see, with filters, sort, paging and the totals of those that are not cancelled.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SalesOrderPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] SalesOrderListQuery query, CancellationToken cancellationToken) =>
        (await reads.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The same list, every page, as an .xlsx file.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] SalesOrderListQuery query, CancellationToken cancellationToken) =>
        (await reads.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>The code the next order would get (DH000001); not reserved.</summary>
    [HttpGet("next-code")]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<SalesOrderNextCode>(StatusCodes.Status200OK)]
    public async Task<IActionResult> NextCode(CancellationToken cancellationToken) => (await reads.NextCodeAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>One order with its lines, advances, progress, the buttons the caller may press (<c>allowedActions</c>) and the purchase request it came from.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SalesOrderView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => (await reads.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>The order with this code (regardless of letter case): the link <c>?code=DH000001</c> opens it.</summary>
    [HttpGet("by-code/{code}")]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SalesOrderView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByCode(string code, CancellationToken cancellationToken) => (await reads.GetByCodeAsync(code, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// "Xử lý đặt hàng", SALES_INVOICES Create in some zone: the invoice the order proposes to make now (the lines still to deliver at the order price and unit, the lot proposed for each, the advance left
    /// and the share of the order discount). Changes nothing. 409 MSG-ORDER-05 when the order is not Draft or Delivering.
    /// </summary>
    [HttpGet("{id:guid}/invoice-draft")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceDraft>(StatusCodes.Status200OK)]
    public async Task<IActionResult> InvoiceDraft(Guid id, CancellationToken cancellationToken) => (await invoiceDrafts.DraftAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Owner only: the form of an order made from a purchase request. 409 MSG-ORDER-02 when it already has an order, MSG-GEN-09 when it cannot be ordered.</summary>
    [HttpGet("request-draft/{requestId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<SalesOrderRequestDraft>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestDraft(Guid requestId, CancellationToken cancellationToken) => (await reads.RequestDraftAsync(requestId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Saves a Draft order, with the advance when there is one (one save), and for the Owner with <c>purchaseRequestId</c> turns the request into Ordered and tells the trader. 409 MSG-SALE-10 asks a question
    /// (a price other than the list price, a discount), answered by the flag in <c>confirmationField</c>; 409 MSG-GEN-02 for a code taken (field Code); 409 MSG-ORDER-02, MSG-GEN-09 for the request;
    /// 400 MSG-GEN-01 names every invalid field (a walk-in buyer pays no advance: field Advance).
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<SalesOrderSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveSalesOrderRequest request, CancellationToken cancellationToken) =>
        (await orders.CreateAsync(request, cancellationToken)).ToActionResult(o => StatusCode(StatusCodes.Status201Created, o));

    /// <summary>
    /// Replaces the header and the lines of a Draft or Delivering order (the row version is required) and, with an <c>advance</c> block, takes a new advance in the same save (the rules of the advance endpoint;
    /// <c>advanceQr</c> in the answer for VietQr). 409 MSG-ORDER-03 for a quantity below what was delivered.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<SalesOrderSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveSalesOrderRequest request, CancellationToken cancellationToken) =>
        (await orders.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Adds an advance to an open order: SALES_ORDERS Edit or CUSTOMER_PAYMENTS Create (the service checks which). The total of the advances stays at most the total of the order; a buyer with no
    /// profile pays none (field Advance). <c>method</c> VietQr makes a code that waits for the transfer (<c>qr</c> in the answer).
    /// </summary>
    [HttpPost("{id:guid}/advances")]
    [ProducesResponseType<SalesOrderAdvanceSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddAdvance(Guid id, SalesOrderAdvanceInput request, CancellationToken cancellationToken) =>
        (await advances.AddAsync(id, request, cancellationToken)).ToActionResult(a => StatusCode(StatusCodes.Status201Created, a));

    /// <summary>"Kết thúc": a Delivering order becomes Completed; what is left of its advance is general credit of the customer from then on.</summary>
    [HttpPost("{id:guid}/finish")]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<SalesOrderClosed>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Finish(Guid id, FinishSalesOrderRequest request, CancellationToken cancellationToken) =>
        (await orders.FinishAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Cancels an order with a reason (Owner: SALES_ORDERS Delete). 409 MSG-ORDER-01 while an invoice that is not cancelled was made from it. The advance is left as it is and, the order being cancelled,
    /// becomes general credit (<c>creditLeft</c>).
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.Delete, PermissionScope.NoZone)]
    [ProducesResponseType<SalesOrderClosed>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelSalesOrderRequest request, CancellationToken cancellationToken) =>
        (await orders.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>One order as an .xlsx file.</summary>
    [HttpGet("{id:guid}/print")]
    [RequirePermission(PermissionFunctions.SalesOrders, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Print(Guid id, CancellationToken cancellationToken) =>
        (await reads.PrintAsync(id, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
