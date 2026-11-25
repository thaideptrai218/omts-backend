using Microsoft.Extensions.Logging;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;

namespace OMTS.Infrastructure.Messaging;

/// <summary>
/// <c>Sms:Provider = None</c>, the default outside Development until a real provider exists: every send
/// fails with <c>NOT_CONFIGURED</c>, so callers answer MSG-ACCOUNT-13 instead of reporting a code as sent.
/// </summary>
internal sealed class UnconfiguredSmsSender(ILogger<UnconfiguredSmsSender> logger) : ISmsSender
{
    public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("SMS to {Recipient} not sent: no SMS provider is configured", Masking.Phone(phoneNumber));
        return Task.FromResult(DeliveryResult.Failed(UnconfiguredSenders.NotConfigured));
    }
}

internal static class UnconfiguredSenders
{
    public const string NotConfigured = "NOT_CONFIGURED";
}
