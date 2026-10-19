namespace OMTS.Application.Features.Authorization.Models;

/// <summary>Zones in which an action is allowed: everywhere, or only the listed zones.</summary>
public sealed record PermittedZones(bool AllZones, IReadOnlySet<Guid> ZoneIds)
{
    public static readonly PermittedZones None = new(false, new HashSet<Guid>());
    public static readonly PermittedZones Everywhere = new(true, new HashSet<Guid>());

    public bool IsEmpty => !AllZones && ZoneIds.Count == 0;
}
