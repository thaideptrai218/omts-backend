namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Filters of the audit search (ACCOUNT-10). The period is always set; the other filters are optional.</summary>
/// <param name="ToExclusive">The start of the day after the last day of the period.</param>
/// <param name="SystemActor">Only the events of the system: neither an account nor an employee acted.</param>
/// <param name="RecordCodePrefix">Record codes that start with this text.</param>
public sealed record AuditEventFilter(
    DateTimeOffset From,
    DateTimeOffset ToExclusive,
    Guid? ActorAccountId = null,
    Guid? ActorEmployeeId = null,
    bool SystemActor = false,
    string? Action = null,
    string? RecordType = null,
    string? RecordCodePrefix = null,
    Guid? ZoneId = null);

/// <summary>An audit event as listed, with the names of who acted (none for the system).</summary>
public sealed record AuditEventRow(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorAccountId,
    string? ActorAccountName,
    string? ActorUsername,
    Guid? ActorEmployeeId,
    string? ActorEmployeeName,
    string Action,
    string RecordType,
    Guid? RecordId,
    string? RecordCode,
    Guid? ZoneId,
    string? Reason);

/// <summary>One event with its stored before/after values (JSON).</summary>
public sealed record AuditEventValues(AuditEventRow Event, string? Before, string? After);

/// <summary>An account or an employee that acted in at least one event.</summary>
public sealed record AuditActorRow(Guid? AccountId, Guid? EmployeeId, string Name, string? Username);

/// <summary>The values the audit search offers in its filters.</summary>
public sealed record AuditFacetRows(IReadOnlyList<string> Actions, IReadOnlyList<string> RecordTypes, IReadOnlyList<AuditActorRow> People);

/// <summary>Reads the audit log (ACCOUNT-10). Writing stays with <see cref="IAuditEventRepository"/>; nothing here changes data.</summary>
public interface IAuditEventQuery
{
    /// <summary>The events of the filter, newest first, one page, and how many match in all.</summary>
    Task<(IReadOnlyList<AuditEventRow> Items, int Total)> SearchAsync(
        AuditEventFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    Task<AuditEventValues?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The actions, record types and people of the whole log.</summary>
    Task<AuditFacetRows> FacetsAsync(CancellationToken cancellationToken);
}
