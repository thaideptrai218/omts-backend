using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Filters;
using OMTS.Infrastructure.Messaging.Fakes;

namespace OMTS.Api.Controllers.Development;

/// <summary>
/// Development only: what the fake SMS, email and push senders "delivered" (verification codes,
/// notifications), so web/mobile developers and tests can continue a flow without a real provider.
/// </summary>
[ApiController]
[Route("api/dev")]
[AllowAnonymous]
[DevelopmentOnly]
public sealed class DevInboxController(FakeSmsSender sms, FakeEmailSender email, FakePushSender push) : ControllerBase
{
    [HttpGet("sms")]
    public IReadOnlyList<FakeMessage> ListSms(string? phoneNumber) => sms.Inbox.List(phoneNumber);

    [HttpDelete("sms")]
    public IActionResult ClearSms()
    {
        sms.Inbox.Clear();
        return NoContent();
    }

    [HttpGet("email")]
    public IReadOnlyList<FakeMessage> ListEmail(string? to) => email.Inbox.List(to);

    [HttpDelete("email")]
    public IActionResult ClearEmail()
    {
        email.Inbox.Clear();
        return NoContent();
    }

    [HttpGet("push")]
    public IReadOnlyList<FakeMessage> ListPush(Guid? accountId) => push.Inbox.List(accountId?.ToString());

    [HttpDelete("push")]
    public IActionResult ClearPush()
    {
        push.Inbox.Clear();
        return NoContent();
    }
}
