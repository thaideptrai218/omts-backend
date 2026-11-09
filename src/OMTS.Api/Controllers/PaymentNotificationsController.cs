using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Sales.Payments;

namespace OMTS.Api.Controllers;

/// <summary>
/// API-13: the Payment Notification Service reports the transfers to the receiving bank account (GFR-08). Anonymous, so the provider's signature is the only way in: a
/// notification that the provider's adapter does not find authentic answers 401 and is not read. 200 lists what was done with each transfer (a transfer reported again is
/// AlreadyRecorded, so the service may retry safely); 400 for a body the adapter cannot read; 5xx when the save failed, so the service retries.
/// </summary>
[ApiController]
[Route("api/integrations/payment-notifications")]
[AllowAnonymous]
public sealed class PaymentNotificationsController(IBankNotificationParser parser, BankTransferNoticeService notices, ILogger<PaymentNotificationsController> logger)
    : ControllerBase
{
    /// <summary>A notification holds a few transfers; far more is not a notification.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    [HttpPost]
    [EnableRateLimiting(RateLimitingExtensions.PaymentNotifications)]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType<BankNotificationReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var notification = new BankNotificationRequest(headers, buffer.ToArray());
        if (!parser.IsAuthentic(notification))
        {
            // Never the body or the headers: they may hold a guessed signature.
            logger.LogWarning("Payment notification refused: not authentic ({Bytes} bytes)", notification.Body.Length);
            return Unauthorized();
        }

        var parsed = parser.Parse(notification);
        if (parsed.Error is not null)
        {
            return AppError.Invalid("Notification", parsed.Error).ToProblem();
        }

        return Ok(await notices.ReceiveAsync(parsed.Notices, cancellationToken));
    }
}
