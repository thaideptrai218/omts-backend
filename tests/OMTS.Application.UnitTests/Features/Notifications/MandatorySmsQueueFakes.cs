using Microsoft.Extensions.Logging.Abstractions;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>A real <see cref="MandatorySmsQueue"/> over the accounts added here, keeping what it queues. The queue never reads a number, so these are plain labels.</summary>
internal sealed class MandatorySmsQueueFakes : IMandatorySmsRecipientQuery, IMandatorySmsOutbox
{
    private readonly List<MandatorySmsRecipient> _accounts = [];

    public MandatorySmsQueueFakes() => Queue = new MandatorySmsQueue(this, this, NullLogger<MandatorySmsQueue>.Instance);

    public MandatorySmsQueue Queue { get; }

    public List<MandatorySmsMessage> Queued { get; } = [];

    /// <summary>When set, the query fails as a database would.</summary>
    public Exception? QueryFailure { get; set; }

    public MandatorySmsRecipient Owner(Guid? id = null, string? phone = "owner-phone") =>
        Add(new MandatorySmsRecipient(id ?? Guid.NewGuid(), SubjectType.CooperativeOwner, [], phone));

    public MandatorySmsRecipient Staff(string roleCode, string? phone = "staff-phone") =>
        Add(new MandatorySmsRecipient(Guid.NewGuid(), SubjectType.Staff, [roleCode], phone));

    public MandatorySmsRecipient ZoneManager() => Staff(FixedRoles.ZoneManagerCode);

    public MandatorySmsRecipient Accountant() => Staff(FixedRoles.AccountantCode);

    public Task<IReadOnlyList<MandatorySmsRecipient>> ListAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        QueryFailure is { } failure
            ? throw failure
            : Task.FromResult<IReadOnlyList<MandatorySmsRecipient>>(_accounts.Where(a => accountIds.Contains(a.AccountId)).ToList());

    public void Enqueue(MandatorySmsMessage message) => Queued.Add(message);

    private MandatorySmsRecipient Add(MandatorySmsRecipient account)
    {
        _accounts.Add(account);
        return account;
    }
}
