using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>ACCOUNT-10 reads of the audit log; no tracking, nothing saved.</summary>
internal sealed class AuditEventQuery(OmtsDbContext db) : IAuditEventQuery
{
    // The account names come with the event; employees have no foreign key yet, so their names are read afterwards.
    private static readonly Expression<Func<AuditEvent, AuditEventRow>> RowOf = e => new AuditEventRow(
        e.Id,
        e.OccurredAt,
        e.ActorAccountId,
        e.ActorAccount != null ? e.ActorAccount.DisplayName : null,
        e.ActorAccount != null ? e.ActorAccount.Username : null,
        e.ActorEmployeeId,
        null,
        e.Action,
        e.RecordType,
        e.RecordId,
        e.RecordCode,
        e.ZoneId,
        e.Reason);

    public async Task<(IReadOnlyList<AuditEventRow> Items, int Total)> SearchAsync(
        AuditEventFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return ([], 0);
        }

        // Newest first; the id keeps the order of events of the same instant stable from page to page.
        var rows = await query
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(RowOf)
            .ToListAsync(cancellationToken);
        return (await WithEmployeeNamesAsync(rows, cancellationToken), total);
    }

    public async Task<AuditEventValues?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var events = db.AuditEvents.AsNoTracking().Where(e => e.Id == id);
        var row = await events.Select(RowOf).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var values = await events.Select(e => new { e.Before, e.After }).SingleAsync(cancellationToken);
        return new AuditEventValues((await WithEmployeeNamesAsync([row], cancellationToken))[0], values.Before, values.After);
    }

    public async Task<AuditFacetRows> FacetsAsync(CancellationToken cancellationToken)
    {
        var actions = await db.AuditEvents.Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync(cancellationToken);
        var recordTypes = await db.AuditEvents.Select(e => e.RecordType).Distinct().OrderBy(t => t).ToListAsync(cancellationToken);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => db.AuditEvents.Any(e => e.ActorAccountId == a.Id))
            .OrderBy(a => a.DisplayName).ThenBy(a => a.Username)
            .Select(a => new AuditActorRow(a.Id, null, a.DisplayName, a.Username))
            .ToListAsync(cancellationToken);
        var employees = await db.Employees.AsNoTracking()
            .Where(x => db.AuditEvents.Any(e => e.ActorEmployeeId == x.Id))
            .OrderBy(x => x.FullName)
            .Select(x => new AuditActorRow(null, x.Id, x.FullName, null))
            .ToListAsync(cancellationToken);
        return new AuditFacetRows(actions, recordTypes, [.. accounts, .. employees]);
    }

    private IQueryable<AuditEvent> Filtered(AuditEventFilter filter)
    {
        var query = db.AuditEvents.AsNoTracking().Where(e => e.OccurredAt >= filter.From && e.OccurredAt < filter.ToExclusive);
        if (filter.ActorAccountId is { } accountId)
        {
            query = query.Where(e => e.ActorAccountId == accountId);
        }

        if (filter.ActorEmployeeId is { } employeeId)
        {
            query = query.Where(e => e.ActorEmployeeId == employeeId);
        }

        if (filter.SystemActor)
        {
            query = query.Where(e => e.ActorAccountId == null && e.ActorEmployeeId == null);
        }

        if (filter.Action is { } action)
        {
            query = query.Where(e => e.Action == action);
        }

        if (filter.RecordType is { } recordType)
        {
            query = query.Where(e => e.RecordType == recordType);
        }

        if (filter.RecordCodePrefix is { } code)
        {
            // Translated to LIKE 'code%' with the wildcards of the text escaped, so it seeks the RecordCode index.
            query = query.Where(e => e.RecordCode != null && e.RecordCode.StartsWith(code));
        }

        if (filter.ZoneId is { } zoneId)
        {
            query = query.Where(e => e.ZoneId == zoneId);
        }

        return query;
    }

    private async Task<IReadOnlyList<AuditEventRow>> WithEmployeeNamesAsync(IReadOnlyList<AuditEventRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Where(r => r.ActorEmployeeId is not null).Select(r => r.ActorEmployeeId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return rows;
        }

        var names = await db.Employees.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.FullName })
            .ToDictionaryAsync(x => x.Id, x => x.FullName, cancellationToken);
        return rows.Select(r => r.ActorEmployeeId is { } id ? r with { ActorEmployeeName = names.GetValueOrDefault(id) } : r).ToList();
    }
}
