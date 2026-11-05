using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Notifications;

/// <summary>
/// Queues the SMS of a notification for the recipients whose role its type names (<see cref="NotificationSmsRule"/>);
/// the SMS goes out after the caller's save commits, as push does. The actor rule of decision 3 is not decided here: an
/// operational module leaves the acting Owner out of the recipients (<see cref="OwnerActorRecipientFilter"/>), and a
/// money module keeps them, so they get the SMS too. A problem here never fails the business action (GFR-06).
/// </summary>
public sealed class MandatorySmsQueue(IMandatorySmsRecipientQuery recipients, IMandatorySmsOutbox outbox, ILogger<MandatorySmsQueue> logger)
{
    /// <param name="accountIds">The active recipients of the notification.</param>
    public async Task QueueAsync(
        NotificationTypeDefinition definition, NotificationContent content, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        if (definition.Sms is not { } rule || accountIds.Count == 0)
        {
            return;
        }

        try
        {
            var text = NotificationSmsText.Render(definition, rule, content);
            foreach (var recipient in await recipients.ListAsync(accountIds, cancellationToken))
            {
                if (NotificationRoles.OfRoleCodes(recipient.SubjectType, recipient.RoleCodes).Any(rule.To.Contains))
                {
                    outbox.Enqueue(new MandatorySmsMessage(recipient.AccountId, definition.Code, recipient.VerifiedPhoneNumber, text));
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception type only: its message could hold the phone number or the text.
            logger.LogError("Mandatory SMS {Type} not queued: {ErrorType}", definition.Code, exception.GetType().Name);
        }
    }
}
