using OMTS.Application.Common.Models.Messaging;
using OMTS.Application.Common.Interfaces.Messaging;

namespace OMTS.Infrastructure.Messaging;

/// <summary>Per-request queue behind <see cref="IPushOutbox"/>, emptied by <c>PushOutboxInterceptor</c>.</summary>
internal sealed class PushOutbox : IPushOutbox
{
    private readonly List<PushMessage> _pending = [];

    public void Enqueue(PushMessage message) => _pending.Add(message);

    public IReadOnlyList<PushMessage> TakeAll()
    {
        var messages = _pending.ToList();
        _pending.Clear();
        return messages;
    }

    public void Discard() => _pending.Clear();
}
