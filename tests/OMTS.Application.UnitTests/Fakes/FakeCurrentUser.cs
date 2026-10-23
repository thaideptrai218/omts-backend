using OMTS.Application.Common.Interfaces;

namespace OMTS.Application.UnitTests.Fakes;

public sealed class FakeCurrentUser(Guid? accountId) : ICurrentUser
{
    public Guid? AccountId { get; } = accountId;
    public Guid? SessionId { get; } = accountId is null ? null : Guid.NewGuid();
}
