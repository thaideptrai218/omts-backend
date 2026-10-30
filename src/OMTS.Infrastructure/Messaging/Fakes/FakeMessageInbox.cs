using System.Text.Json.Serialization;

namespace OMTS.Infrastructure.Messaging.Fakes;

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
