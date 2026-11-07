using System.Text.Json;
using System.Text.Json.Serialization;

namespace OMTS.Infrastructure.Messaging.Fakes;

/// <summary>A message a fake sender "delivered", kept in memory for the dev-only inbox endpoints and tests.</summary>
/// <param name="Devices">Push only: the device tokens a real provider would deliver to (empty: none).</param>
/// <param name="NotificationId">Push only: the kept notification it announces.</param>
/// <param name="MessageCode">Push only: the SRS code of the content.</param>
/// <param name="MessageParams">Push only: the parameters of <paramref name="MessageCode"/>, the JSON object a device renders.</param>
public sealed record FakeMessage(
    string Id,
    DateTimeOffset SentAt,
    string Recipient,
    string? Subject,
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Devices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? NotificationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MessageCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? MessageParams = null);

/// <summary>Keeps the last <see cref="Capacity"/> messages of one fake channel (SMS, email, push).</summary>
public sealed class FakeMessageInbox
{
    public const int Capacity = 200;

    private readonly LinkedList<FakeMessage> _messages = new();
    private readonly object _gate = new();

    public void Add(FakeMessage message)
    {
        lock (_gate)
        {
            _messages.AddFirst(message);
            if (_messages.Count > Capacity)
            {
                _messages.RemoveLast();
            }
        }
    }

    /// <summary>Newest first; optionally only one recipient's messages.</summary>
    public IReadOnlyList<FakeMessage> List(string? recipient = null)
    {
        lock (_gate)
        {
            return _messages
                .Where(m => recipient is null || string.Equals(m.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
        }
    }
}
