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
/// (records with no single zone, e.g. accounts). A zone license holds a zone, but it is Owner-only and its zone is not
/// checked, so it resolves with none too. A zone held only by a parent record (e.g. a lane, through its raft) is not
/// followed: a zone-checked notification type on such a record needs a parent mapping here first.
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
        // No zone (PM 03/11): the Owner opens it, and the trader of its customer profile.
        if (recordType == RelatedRecordTypes.PurchaseRequest)
        {
            return FindPurchaseRequestAsync(recordId, cancellationToken);
        }

        // The zone of its invoice, and the trader of its customer profile.
        if (recordType == RelatedRecordTypes.QualityClaim)
        {
            return FindQualityClaimAsync(recordId, cancellationToken);
        }

        if (recordType == RelatedRecordTypes.CultivationLog)
        {
            return FindCultivationLogAsync(recordId, cancellationToken);
        }

        // A cycle's zone is its raft's (CYCLE-02 notice).
        if (recordType == RelatedRecordTypes.CultivationCycle)
        {
            return FindCultivationCycleAsync(recordId, cancellationToken);
        }

        // A harvest lot's zone is its raft's (HARVEST-01 notice).
        if (recordType == RelatedRecordTypes.HarvestLot)
        {
            return FindHarvestLotAsync(recordId, cancellationToken);
        }

        var entity = Aliases.TryGetValue(recordType, out var alias)
            ? db.Model.FindEntityType(alias)
            : db.Model.GetEntityTypes().FirstOrDefault(t =>
                t.ClrType.Name == recordType && !t.IsOwned() && !t.HasSharedClrType && typeof(Entity).IsAssignableFrom(t.ClrType));
        if (entity is null)
        {
            return Task.FromResult<RelatedRecordLocation?>(null);
        }

        var zoneType = recordType == RelatedRecordTypes.ZoneLicense ? null : entity.FindProperty(ZoneProperty)?.ClrType;
        var method = zoneType == typeof(Guid) || zoneType == typeof(Guid?)
            ? FindInZoneMethod.MakeGenericMethod(entity.ClrType, zoneType)
            : FindMethod.MakeGenericMethod(entity.ClrType);
        return (Task<RelatedRecordLocation?>)method.Invoke(this, [recordId, cancellationToken])!;
    }

    private async Task<RelatedRecordLocation?> FindCultivationLogAsync(Guid id, CancellationToken cancellationToken)
    {
        var zones = await db.CultivationLogs.AsNoTracking().Where(l => l.Id == id)
            .Select(l => l.Cycle.Raft.ZoneId).ToListAsync(cancellationToken);
        return zones.Count == 0 ? null : new RelatedRecordLocation(zones[0]);
    }

    private async Task<RelatedRecordLocation?> FindCultivationCycleAsync(Guid id, CancellationToken cancellationToken)
    {
        var zones = await db.CultivationCycles.AsNoTracking().Where(c => c.Id == id)
            .Select(c => c.Raft.ZoneId).ToListAsync(cancellationToken);
        return zones.Count == 0 ? null : new RelatedRecordLocation(zones[0]);
    }

    private async Task<RelatedRecordLocation?> FindHarvestLotAsync(Guid id, CancellationToken cancellationToken)
    {
        var zones = await db.HarvestLots.AsNoTracking().Where(l => l.Id == id)
            .Select(l => l.Raft.ZoneId).ToListAsync(cancellationToken);
        return zones.Count == 0 ? null : new RelatedRecordLocation(zones[0]);
    }

    private async Task<RelatedRecordLocation?> FindPurchaseRequestAsync(Guid id, CancellationToken cancellationToken)
    {
        var traders = await db.PurchaseRequests.AsNoTracking().Where(r => r.Id == id).Select(r => r.Customer.AccountId).ToListAsync(cancellationToken);
        return traders.Count == 0 ? null : new RelatedRecordLocation(null, traders[0]);
    }

    private async Task<RelatedRecordLocation?> FindQualityClaimAsync(Guid id, CancellationToken cancellationToken)
    {
        var claims = await db.QualityClaims.AsNoTracking().Where(c => c.Id == id)
            .Select(c => new { c.Invoice.ZoneId, c.Customer.AccountId }).ToListAsync(cancellationToken);
        return claims.Count == 0 ? null : new RelatedRecordLocation(claims[0].ZoneId, claims[0].AccountId);
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
