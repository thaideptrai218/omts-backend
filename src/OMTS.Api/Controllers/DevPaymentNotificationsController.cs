using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Sales.Payments;

namespace OMTS.Api.Controllers;

/// <summary>
/// Development only: fires a transfer as the fake Payment Notification Service would report it, through the same rules as API-13 (GFR-08), so web and mobile can finish a
/// VietQR sale without a bank. Anonymous like the dev inbox; 404 outside Development.
/// </summary>
[ApiController]
[Route("api/dev/payment-notifications")]
[AllowAnonymous]
[DevelopmentOnly]
public sealed class DevPaymentNotificationsController(BankTransferNoticeService notices) : ControllerBase
{
    /// <summary><c>{ invoiceCode, amount }</c> pays to the receiving bank account; send the same <c>bankReference</c> again to replay a notification.</summary>
    [HttpPost]
    [ProducesResponseType<BankNotificationReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Simulate(SimulateBankTransferRequest request, CancellationToken cancellationToken) =>
        (await notices.SimulateAsync(request, cancellationToken)).ToActionResult(Ok);
}
