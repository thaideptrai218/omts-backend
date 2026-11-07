using OMTS.Application.Common.Interfaces.Messaging;

namespace OMTS.Infrastructure.Messaging;

/// <summary>
/// Per-request queue behind <see cref="IPushOutbox"/> and <see cref="IMandatorySmsOutbox"/>, emptied by
/// <c>PushOutboxInterceptor</c>: taking a message removes it, so a later save in the same request cannot send it again.
/// </summary>
internal sealed class PushOutbox : IPushOutbox, IMandatorySmsOutbox
{
    private readonly List<PushMessage> _pending = [];
    private readonly List<MandatorySmsMessage> _pendingSms = [];

    public void Enqueue(PushMessage message) => _pending.Add(message);

    /// <summary>Mandatory SMS are dropped instead of queued: the demo seed, when its sender could reach a real phone.</summary>
    public bool DropsMandatorySms { get; set; }

    public void Enqueue(MandatorySmsMessage message)
    {
        if (!DropsMandatorySms)
        {
            _pendingSms.Add(message);
        }
    }

    public IReadOnlyList<PushMessage> TakeAll()
    {
        var messages = _pending.ToList();
        _pending.Clear();
        return messages;
    }

    public IReadOnlyList<MandatorySmsMessage> TakeAllSms()
    {
        var messages = _pendingSms.ToList();
        _pendingSms.Clear();
        return messages;
    }

    public void Discard()
    {
        _pending.Clear();
        _pendingSms.Clear();
    }
}
