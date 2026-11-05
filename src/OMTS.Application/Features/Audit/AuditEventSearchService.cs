using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit.Dtos;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.Features.Audit;

/// <summary>
/// ACCOUNT-10 – search the audit log, open an event with its values before and after, and open its record as it is now.
/// The Cooperative Owner sees every event (BR-ACCOUNT-13; the API lets no one else in). Reading changes no data, and
/// the log has no edit or delete.
/// </summary>
public sealed class AuditEventSearchService(IAuditEventQuery events, IRelatedRecordQuery records, TimeProvider timeProvider)
{
    public const int MaxPageSize = 100;
    public const int DefaultPeriodDays = 30;

    /// <summary>The longest period, so one request never reads more than the 5 years the log is kept for (NFR-02).</summary>
    public const int MaxPeriodYears = 5;

    /// <summary>Earliest date of the period; the latest is one year after today, so no date overflows.</summary>
    public static readonly DateOnly Earliest = new(2000, 1, 1);

    private const int MaxFilterLength = 100;
    private const string SystemName = "System";

    public async Task<Result<AuditEventSearchResult>> SearchAsync(AuditEventQuery query, CancellationToken cancellationToken)
    {
        var filter = Filter(query);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // The page is capped so (page - 1) * pageSize cannot overflow into a negative OFFSET.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var (items, total) = await events.SearchAsync(filter.Value!.Filter, page, pageSize, cancellationToken);
        var empty = total == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new AuditEventSearchResult(
            new Paged<AuditEventListItem>(items.Select(Item).ToList(), page, pageSize, total),
            filter.Value.From,
            filter.Value.To,
            empty?.Text,
            empty?.Code);
    }

    /// <summary>ACCOUNT-10 step 5: the event with its values before and after, secrets removed again for display.</summary>
    public async Task<Result<AuditEventDetail>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await events.GetAsync(id, cancellationToken) is not { } found)
        {
            return AppError.NotFound;
        }

        var e = found.Event;
        return new AuditEventDetail(
            e.Id, e.OccurredAt, Actor(e), e.Action, e.RecordType, e.RecordId, e.RecordCode, e.ZoneId, e.Reason,
            AuditValueSerializer.ForDisplay(found.Before), AuditValueSerializer.ForDisplay(found.After));
    }

    public async Task<AuditEventFacets> FacetsAsync(CancellationToken cancellationToken)
    {
        var facets = await events.FacetsAsync(cancellationToken);
        var people = facets.People.Select(p => p.AccountId is { } accountId
            ? new AuditActor(AuditActorKind.Account, accountId, null, p.Name, p.Username)
            : new AuditActor(AuditActorKind.Employee, null, p.EmployeeId, p.Name, null)).ToList();
        return new AuditEventFacets(facets.Actions, facets.RecordTypes, people);
    }

    /// <summary>
    /// ACCOUNT-10.1: the record of the event when it still exists, else MSG-ACCOUNT-22 (10.1.E1); the event itself stays
    /// available. An event that names no record (e.g. a refused creation) has none to open either.
    /// </summary>
    public async Task<Result<AuditEventRecord>> OpenRecordAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await events.GetAsync(id, cancellationToken) is not { Event: var e })
        {
            return AppError.NotFound;
        }

        // The same resolver as the notifications (ACCOUNT-08); the Owner sees every record, so its zone is not checked.
        if (e.RecordId is { } recordId && await records.FindAsync(e.RecordType, recordId, cancellationToken) is not null)
        {
            return new AuditEventRecord(e.Id, e.RecordType, recordId, e.RecordCode);
        }

        return new AppError(
            ErrorKind.NotFound, UserMessages.RecordNoLongerExists(RelatedRecordTypes.ObjectName(e.RecordType), e.RecordCode ?? ""));
    }

    private Result<(AuditEventFilter Filter, DateOnly From, DateOnly To)> Filter(AuditEventQuery query)
    {
        var today = timeProvider.Today();
        var latest = today.AddYears(1);
        if (query.From is { } f && (f < Earliest || f > latest))
        {
            return AppError.Invalid("From date");
        }

        if (query.To is { } t && (t < Earliest || t > latest))
        {
            return AppError.Invalid("To date");
        }

        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(1 - DefaultPeriodDays);
        if (from > to)
        {
            return AppError.Invalid("From date", "From must not be after To.");
        }

        if (to > from.AddYears(MaxPeriodYears))
        {
            // Only "from" entered: "to" is today, so the date to fix is the one entered.
            return AppError.Invalid(query.To is null ? "From date" : "To date", $"The period is at most {MaxPeriodYears} years.");
        }

        var people = (query.ActorAccountId is null ? 0 : 1) + (query.ActorEmployeeId is null ? 0 : 1) + (query.SystemActor ? 1 : 0);
        if (people > 1)
        {
            return AppError.Invalid("Person", "Choose one account, one employee or the system.");
        }

        // A text longer than the stored column can never match: refused rather than searched.
        var (action, recordType, recordCode) = (Trimmed(query.Action), Trimmed(query.RecordType), Trimmed(query.RecordCode));
        var tooLong = action?.Length > MaxFilterLength ? "Action"
            : recordType?.Length > MaxFilterLength ? "Record type"
            : recordCode?.Length > MaxFilterLength ? "Record code"
            : null;
        if (tooLong is not null)
        {
            return AppError.Invalid(tooLong);
        }

        var filter = new AuditEventFilter(
            StartOf(from), StartOf(to.AddDays(1)), query.ActorAccountId, query.ActorEmployeeId, query.SystemActor,
            action, recordType, recordCode, query.ZoneId);
        return (filter, from, to);
    }

    /// <summary>Empty means no filter.</summary>
    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset StartOf(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);

    private static AuditEventListItem Item(AuditEventRow e) =>
        new(e.Id, e.OccurredAt, Actor(e), e.Action, e.RecordType, e.RecordId, e.RecordCode, e.ZoneId, e.Reason);

    // An employee-app action names the employee even when an account is also stored (GBR-02).
    private static AuditActor Actor(AuditEventRow e) => (e.ActorEmployeeId, e.ActorAccountId) switch
    {
        ({ } employeeId, _) => new AuditActor(AuditActorKind.Employee, e.ActorAccountId, employeeId, e.ActorEmployeeName ?? "Employee", null),
        (null, { } accountId) => new AuditActor(AuditActorKind.Account, accountId, null, e.ActorAccountName ?? "Account", e.ActorUsername),
        _ => new AuditActor(AuditActorKind.System, null, null, SystemName, null),
    };
}
