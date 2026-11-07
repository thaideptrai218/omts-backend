using System.Text.Json;
using OMTS.Application.Features.AccessManagement.Dtos;

namespace OMTS.Application.Features.Audit.Dtos;

/// <summary>
/// ACCOUNT-10 filters. The period is Vietnam dates, both ends included (default: the last 30 days up to today). The
/// person is one account, one employee or the system, never two of them.
/// </summary>
/// <param name="SystemActor">Only the automatic actions of the system.</param>
/// <param name="RecordCode">Matches the record codes that start with it.</param>
public sealed record AuditEventQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? ActorAccountId = null,
    Guid? ActorEmployeeId = null,
    bool SystemActor = false,
    string? Action = null,
    string? RecordType = null,
    string? RecordCode = null,
    Guid? ZoneId = null,
    int Page = 1,
    int PageSize = 20);

public enum AuditActorKind
{
    System,
    Account,
    Employee,
}

/// <summary>Who acted: an account (display name and username), an employee (full name) or the system ("System").</summary>
public sealed record AuditActor(AuditActorKind Kind, Guid? AccountId, Guid? EmployeeId, string Name, string? Username);

/// <param name="RecordType">The entity name, e.g. "FarmingZone"; the values are listed by the facets.</param>
public sealed record AuditEventListItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    AuditActor Actor,
    string Action,
    string RecordType,
    Guid? RecordId,
    string? RecordCode,
    Guid? ZoneId,
    string? Reason);

/// <summary>An event with its values before and after the change (ACCOUNT-10 step 5); never a password, code or token.</summary>
public sealed record AuditEventDetail(
    Guid Id,
    DateTimeOffset OccurredAt,
    AuditActor Actor,
    string Action,
    string RecordType,
    Guid? RecordId,
    string? RecordCode,
    Guid? ZoneId,
    string? Reason,
    JsonElement? Before,
    JsonElement? After);

/// <param name="From">The first day of the period searched.</param>
/// <param name="To">The last day of the period searched.</param>
/// <param name="Message">MSG-GEN-10 when no event matches (ACCOUNT-10.0.E1).</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record AuditEventSearchResult(
    Paged<AuditEventListItem> Page, DateOnly From, DateOnly To, string? Message, string? MessageCode);

/// <summary>The values of the filters: the actions and record types written so far, and the accounts and employees that acted.</summary>
public sealed record AuditEventFacets(IReadOnlyList<string> Actions, IReadOnlyList<string> RecordTypes, IReadOnlyList<AuditActor> People);

/// <summary>The record of an event as it is now, to open in the use case that manages it (ACCOUNT-10.1).</summary>
public sealed record AuditEventRecord(Guid EventId, string RecordType, Guid RecordId, string? RecordCode);
