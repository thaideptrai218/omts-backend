namespace OMTS.Application.Features.Testing;

/// <summary>Opt-in local Development tooling. Never enable it on a shared production deployment.</summary>
public sealed class TestRoleOptions
{
    public bool Enabled { get; set; }
    public Guid OwnerAccountId { get; set; }
}
