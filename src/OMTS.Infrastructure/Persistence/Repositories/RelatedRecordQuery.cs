using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// The record of a notification or an audit event. Record types are entity names (or an alias below), so the records of
/// every module resolve without a list to keep up to date; a type no entity has any more cannot be opened, as a removed
/// record. The zone is the record itself for a farming zone, else its <c>ZoneId</c> when the entity has one, else none
/// (records with no single zone, e.g. accounts and zone licenses, which are Owner-only).
/// </summary>
internal sealed class RelatedRecordQuery(OmtsDbContext db) : IRelatedRecordQuery
{
    private const string ZoneProperty = nameof(Raft.ZoneId);

    /// <summary>Record types that are not the name of their entity.</summary>
    private static readonly IReadOnlyDictionary<string, Type> Aliases = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [RelatedRecordTypes.ZoneLicense] = typeof(ZoneComplianceDocument),
    };

    private static readonly MethodInfo FindMethod =
        typeof(RelatedRecordQuery).GetMethod(nameof(FindRecordAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo FindInZoneMethod =
        typeof(RelatedRecordQuery).GetMethod(nameof(FindRecordInZoneAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public Task<RelatedRecordLocation?> FindAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var entity = Aliases.TryGetValue(recordType, out var alias)
            ? db.Model.FindEntityType(alias)
            : db.Model.GetEntityTypes().FirstOrDefault(t =>
                t.ClrType.Name == recordType && !t.IsOwned() && !t.HasSharedClrType && typeof(Entity).IsAssignableFrom(t.ClrType));
        if (entity is null)
        {
            return Task.FromResult<RelatedRecordLocation?>(null);
        }

        var zoneType = entity.FindProperty(ZoneProperty)?.ClrType;
        var method = zoneType == typeof(Guid) || zoneType == typeof(Guid?)
            ? FindInZoneMethod.MakeGenericMethod(entity.ClrType, zoneType)
            : FindMethod.MakeGenericMethod(entity.ClrType);
        return (Task<RelatedRecordLocation?>)method.Invoke(this, [recordId, cancellationToken])!;
    }

    private async Task<RelatedRecordLocation?> FindRecordAsync<T>(Guid id, CancellationToken cancellationToken) where T : Entity =>
        await db.Set<T>().AnyAsync(r => r.Id == id, cancellationToken)
            ? new RelatedRecordLocation(typeof(T) == typeof(FarmingZone) ? id : null)
            : null;

    /// <typeparam name="TZone"><see cref="Guid"/> or a nullable one, as the entity's <c>ZoneId</c>.</typeparam>
    private async Task<RelatedRecordLocation?> FindRecordInZoneAsync<T, TZone>(Guid id, CancellationToken cancellationToken) where T : Entity
    {
        var zones = await db.Set<T>().AsNoTracking().Where(r => r.Id == id)
            .Select(r => EF.Property<TZone>(r, ZoneProperty))
            .ToListAsync(cancellationToken);
        return zones.Count == 0 ? null : new RelatedRecordLocation((Guid?)(object?)zones[0]);
    }
}
