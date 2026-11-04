using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Messaging;

namespace OMTS.Infrastructure.Messaging;

/// <summary>
/// Sends the mandatory SMS of a committed save. Never throws and never holds the answer longer than
/// <see cref="SmsOptions.MandatoryTimeoutSeconds"/>, also with a provider that ignores its cancellation, so a failed or
/// slow SMS leaves the business action as it is (GFR-06). Nothing is retried: a provider that timed out may have
/// delivered, and a second send could reach the phone twice. Logs hold the type and the account id only, never the
/// phone number, the text or what a provider answered, which could repeat them.
/// </summary>
internal sealed class MandatorySmsDelivery(ISmsSender sender, IOptions<SmsOptions> options, ILogger<MandatorySmsDelivery> logger)
{
    public async Task SendAsync(IReadOnlyList<MandatorySmsMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        try
        {
            await SendAllAsync(messages);
        }
        catch (Exception exception)
        {
            // The save is committed: nothing may reach its caller. The exception type only, as below.
            logger.LogError("{Count} mandatory SMS not sent: {ErrorType}", messages.Count, exception.GetType().Name);
        }
    }

    private async Task SendAllAsync(IReadOnlyList<MandatorySmsMessage> messages)
    {
        // One log for the whole save, not one per recipient and not the per-number warning of the unconfigured sender.
        if (options.Value.Provider == SmsOptions.NoneProvider)
        {
            logger.LogWarning(
                "{Count} mandatory SMS of {Types} not sent: Sms:Provider is None.", messages.Count, string.Join(", ", messages.Select(m => m.Type).Distinct()));
            return;
        }

        var withPhone = new List<MandatorySmsMessage>();
        foreach (var message in messages)
        {
            if (message.PhoneNumber is null)
            {
                logger.LogWarning("Mandatory SMS {Type} to account {AccountId} not sent: the account has no verified phone number.", message.Type, message.RecipientAccountId);
            }
            else
            {
                withPhone.Add(message);
            }
        }

        if (withPhone.Count > 0)
        {
            await SendWithinTimeoutAsync(withPhone);
        }
    }

    private async Task SendWithinTimeoutAsync(IReadOnlyList<MandatorySmsMessage> messages)
    {
        var seconds = options.Value.MandatoryTimeoutSeconds;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var token = timeout.Token;
        // Task.Run: a provider that blocks before its first await must not hold the others or the answer.
        var sends = messages.Select(message => Task.Run(() => SendOneAsync(message, token), CancellationToken.None)).ToList();
        try
        {
            await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (TimeoutException)
        {
            logger.LogWarning("{Count} mandatory SMS had no answer after {Seconds} seconds; the save is kept.", sends.Count(s => !s.IsCompleted), seconds);
        }
    }

    private async Task SendOneAsync(MandatorySmsMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var result = await sender.SendAsync(message.PhoneNumber!, message.Text, cancellationToken);
            if (!result.Succeeded)
            {
                // A fixed outcome: a provider's error code could repeat the phone number or the text.
                logger.LogWarning("Mandatory SMS {Type} to account {AccountId} failed: the provider refused it.", message.Type, message.RecipientAccountId);
            }
        }
        catch (Exception exception)
        {
            // The exception type only: a provider's message could hold the phone number or the text.
            logger.LogWarning("Mandatory SMS {Type} to account {AccountId} failed: {ErrorType}", message.Type, message.RecipientAccountId, exception.GetType().Name);
        }
    }
}
