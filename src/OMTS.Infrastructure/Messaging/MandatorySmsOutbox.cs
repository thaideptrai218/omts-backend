using OMTS.Application.Common.Interfaces.Messaging;

namespace OMTS.Infrastructure.Messaging;

/// <summary>
/// Per-request queue behind <see cref="IMandatorySmsOutbox"/>, emptied by
/// <c>MandatorySmsOutboxInterceptor</c>: taking a message removes it, so a later save in the same request cannot send it again.
/// </summary>
internal sealed class MandatorySmsOutbox : IMandatorySmsOutbox
{
    private readonly List<MandatorySmsMessage> _pendingSms = [];

    /// <summary>Mandatory SMS are dropped instead of queued: the demo seed, when its sender could reach a real phone.</summary>
    public bool DropsMandatorySms { get; set; }

    public void Enqueue(MandatorySmsMessage message)
    {
        if (!DropsMandatorySms)
        {
            _pendingSms.Add(message);
        }
    }

    public IReadOnlyList<MandatorySmsMessage> TakeAllSms()
    {
        var messages = _pendingSms.ToList();
        _pendingSms.Clear();
        return messages;
    }

    public void Discard() => _pendingSms.Clear();
}
