namespace OMTS.Application.Common.Interfaces.Messaging;

/// <summary>
/// The SMS of the mandatory notifications waiting for the current unit of work. They share the queue and the lifecycle of
/// the push messages (<see cref="IPushOutbox"/>): sent once, only after the save commits, and dropped when it fails.
/// </summary>
public interface IMandatorySmsOutbox
{
    void Enqueue(MandatorySmsMessage message);
}

/// <summary>One SMS of a mandatory notification. Never log this record: it holds the phone number and the text.</summary>
/// <param name="Type">The notification type; the only part of the content that goes to the logs.</param>
/// <param name="PhoneNumber">The verified phone number of the account; null when it has none, so no SMS and a log line.</param>
public sealed record MandatorySmsMessage(Guid RecipientAccountId, string Type, string? PhoneNumber, string Text);
