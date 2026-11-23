using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Sales.Checkout;
using OMTS.Application.Features.Sales.Checkout.Dtos;

namespace OMTS.Api.Controllers.Sales;

/// <summary>
/// The logged-in trader pays one invoice still owed from the Zalo Mini App (decision of 21/11/2026): Zalo's Checkout SDK with payOS. Only the trader's own invoices and
/// checkouts: another one answers 404. The web does not use these routes; its VietQR flow stays as it is. MSG04 (503) while the Checkout keys are not set.
/// </summary>
[ApiController]
[Authorize]
[RequireTrader]
[Route("api/me")]
public sealed class MyCheckoutsController(TraderCheckoutService checkouts) : ControllerBase
{
    /// <summary>
    /// 201 with a new checkout of what is left to pay; 200 with the checkout of the invoice still being paid (same amount, link not about to expire). 409 MSG-GEN-09 for
    /// an invoice already paid or cancelled.
    /// </summary>
    [HttpPost("invoices/{invoiceId:guid}/checkout")]
    [ProducesResponseType<TraderCheckoutView>(StatusCodes.Status201Created)]
    [ProducesResponseType<TraderCheckoutView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Start(Guid invoiceId, CancellationToken cancellationToken) =>
        (await checkouts.StartAsync(invoiceId, cancellationToken)).ToActionResult(r => r.Created ? StatusCode(StatusCodes.Status201Created, r.Checkout) : Ok(r.Checkout));

    /// <summary>Polled every few seconds by the QR screen: the server's reading of the payment, never the client's.</summary>
    [HttpGet("checkouts/{id:guid}")]
    [ProducesResponseType<TraderCheckoutView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await checkouts.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>The Zalo order of <c>PAY_BY_CUSTOM_METHOD</c>; Zalo is told about it once the money arrives.</summary>
    [HttpPost("checkouts/{id:guid}/zalo-order")]
    [ProducesResponseType<TraderCheckoutView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> LinkZaloOrder(Guid id, LinkZaloOrderRequest request, CancellationToken cancellationToken) =>
        (await checkouts.LinkZaloOrderAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
