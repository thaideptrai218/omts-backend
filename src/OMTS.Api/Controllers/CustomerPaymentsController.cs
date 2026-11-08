using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>
/// DEBT-02 – customer payments, money paid back, their confirmation and cancellation, for the Accountant and the Owner (§I.4.4; the Zone Manager and the trader have no
/// access). They have no zone: any Effective pair of a role holding CUSTOMER_PAYMENTS opens them (BR-FARM-08), as the customer debts (DEBT-03).
/// </summary>
[ApiController]
[Route("api/customer-payments")]
[Authorize]
public sealed class CustomerPaymentsController(CustomerPaymentService payments, CustomerPaymentStatusService statuses) : ControllerBase
{
    /// <summary>Step 1 / DEBT-02.3 step 2: the debt today, the money owed back, the unpaid invoices oldest first and the people who hand over cash.</summary>
    [HttpGet("form/{customerId:guid}")]
    [RequirePermission(PermissionFunctions.CustomerPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerPaymentForm>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Form(Guid customerId, CancellationToken cancellationToken) =>
        (await payments.FormAsync(customerId, cancellationToken)).ToActionResult(Ok);

    /// <summary>DEBT-02.2 step 2: the payments Awaiting confirmation, oldest first; MSG-GEN-11 when there is none.</summary>
    [HttpGet("awaiting-confirmation")]
    [RequirePermission(PermissionFunctions.CustomerPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<AwaitingCustomerPayments>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Awaiting(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
        Ok(await statuses.AwaitingAsync(page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.CustomerPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerPaymentDetails>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await payments.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Steps 2-7: a payment, Confirmed when saved. 409 MSG-DEBT-04 until <c>confirmAboveDebt</c>, then 409 MSG-DEBT-05 until <c>confirmDuplicateBankReference</c>
    /// (the field is named in <c>confirmationField</c>). A repeated <c>deduplicationKey</c> returns the first payment and creates nothing.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.CustomerPayments, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerPaymentSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(RecordCustomerPaymentRequest request, CancellationToken cancellationToken) =>
        (await payments.RecordAsync(request, CustomerPaymentKind.Payment, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>DEBT-02.3: money paid back, at most the money owed back (400 MSG-DEBT-06); 409 MSG-DEBT-05 as for a payment.</summary>
    [HttpPost("pay-back")]
    [RequirePermission(PermissionFunctions.CustomerPayments, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerPaymentSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> PayBack(RecordCustomerPaymentRequest request, CancellationToken cancellationToken) =>
        (await payments.RecordAsync(request, CustomerPaymentKind.PaidBack, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>DEBT-02.2 steps 3-6: confirm and cancel from the list in one save; one notice to the Owner. 409 MSG-GEN-09 when a payment is no longer Awaiting confirmation.</summary>
    [HttpPost("confirmations")]
    [RequirePermission(PermissionFunctions.CustomerPayments, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerPaymentsConfirmed>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Confirm(ConfirmCustomerPaymentsRequest request, CancellationToken cancellationToken) =>
        (await statuses.ConfirmAsync(request, cancellationToken)).ToActionResult(Ok);

    /// <summary>DEBT-02.4: a payment or money paid back that is not cancelled, with a reason; 409 MSG-GEN-09 when it is already cancelled.</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.CustomerPayments, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerPaymentSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelCustomerPaymentRequest request, CancellationToken cancellationToken) =>
        (await statuses.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
