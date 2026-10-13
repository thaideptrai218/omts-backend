namespace OMTS.Domain.Common;

/// <summary>
/// Base type for domain entities. Identity is a stable Guid so records created
/// offline on mobile devices can be synchronised without key collisions.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected init; } = Guid.NewGuid();
}
