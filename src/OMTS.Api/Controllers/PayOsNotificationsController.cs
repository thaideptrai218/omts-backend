using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Extensions;
using OMTS.Application.Features.Sales.Checkout;
using OMTS.Application.Features.Sales.Checkout.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// payOS reports the transfers of the traders' Mini App checkouts (the webhook URL of the payOS channel). Anonymous, so the signature of the checksum key is the only way in:
/// a notification that is not authentic answers 401 and is not read. 200 says what was done with the transfer (a notification sent again records nothing, so payOS may retry
/// safely), also for the test notification payOS sends when the webhook is registered; 5xx when the save failed, so payOS retries.
/// </summary>
[ApiController]
[Route("api/integrations/payos")]
[AllowAnonymous]
public sealed class PayOsNotificationsController(PayOsNoticeService notices, ILogger<PayOsNotificationsController> logger) : ControllerBase
{
    /// <summary>A payOS notification is one transfer; far more is not one.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    [HttpPost]
    [EnableRateLimiting(RateLimitingExtensions.PaymentNotifications)]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType<PayOsNoticeResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var result = await notices.ReceiveAsync(buffer.ToArray(), cancellationToken);
        if (result is null)
        {
            // Never the body: it may hold a guessed signature.
            logger.LogWarning("payOS notification refused: not authentic ({Bytes} bytes)", buffer.Length);
            return Unauthorized();
        }

        return Ok(result);
    }
}
