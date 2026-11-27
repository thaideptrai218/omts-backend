using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Authorization.Models;

/// <summary>Zones in which an action is allowed: everywhere, or only the listed zones.</summary>
public sealed record PermittedZones(bool AllZones, IReadOnlySet<Guid> ZoneIds)
{
    /// <summary>A longer list is not a choice anyone made on a screen, and would make the query text grow without limit.</summary>
    public const int MaxChosen = 200;

    public static readonly PermittedZones None = new(false, new HashSet<Guid>());
    public static readonly PermittedZones Everywhere = new(true, new HashSet<Guid>());

    public bool IsEmpty => !AllZones && ZoneIds.Count == 0;

    public bool Allows(Guid zoneId) => AllZones || ZoneIds.Contains(zoneId);

    /// <summary>
    /// The zones a list was asked for: the distinct <paramref name="zoneIds"/> and the legacy single <paramref name="zoneId"/> together. More than <see cref="MaxChosen"/>
    /// zone ids is MSG-GEN-01 on <c>ZoneIds</c>.
    /// </summary>
    public static Result<IReadOnlyList<Guid>> Choose(Guid? zoneId, IReadOnlyCollection<Guid>? zoneIds)
    {
        var chosen = (zoneIds ?? []).Distinct().ToList();
        if (chosen.Count > MaxChosen)
        {
            return AppError.Invalid("ZoneIds");
        }

        if (zoneId is { } single && !chosen.Contains(single))
        {
            chosen.Add(single);
        }

        return chosen;
    }

    /// <summary>
    /// The zones to filter a list by: the <paramref name="chosen"/> zones this caller may read, a zone they may not read being left out without a refusal. Nothing chosen means every
    /// permitted zone; chosen zones none of which is permitted is <see cref="NarrowedZones.IsEmpty"/>, never every zone.
    /// </summary>
    public NarrowedZones Narrow(IReadOnlyCollection<Guid>? chosen)
    {
        if (chosen is null || chosen.Count == 0)
        {
            return AllZones ? new NarrowedZones(null, false) : new NarrowedZones(ZoneIds, ZoneIds.Count == 0);
        }

        var kept = chosen.Where(Allows).ToHashSet();
        return new NarrowedZones(kept, kept.Count == 0);
    }
}

/// <param name="ZoneIds">The zones to keep; null when the list is not narrowed by zone. An empty set (when <paramref name="IsEmpty"/>) keeps no row, so it can be handed to a filter as it is.</param>
/// <param name="IsEmpty">True when no zone is left to read: the answer is an empty page, never every zone.</param>
public sealed record NarrowedZones(IReadOnlySet<Guid>? ZoneIds, bool IsEmpty);
