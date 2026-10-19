namespace OMTS.Application.Features.Audit.Models;

/// <summary>
/// One significant change to record (NFR-02): what was done to which record, the values before and
/// after, and the reason when the UC requires one.
/// </summary>
/// <param name="Action">What happened, e.g. "AccessGrant.Revoked" or "Employee.Updated".</param>
/// <param name="RecordType">Entity name of the changed record, e.g. "AccessGrant".</param>
public sealed record AuditEntry(string Action, string RecordType)
{
    public Guid? RecordId { get; init; }

    /// <summary>Business code shown to users (order code, lot code…), when the record has one.</summary>
    public string? RecordCode { get; init; }

    /// <summary>
    /// Snapshot of the changed values, usually an anonymous object with only the fields that changed.
    /// Passwords, verification codes and tokens are removed before storing (NFR-01).
    /// </summary>
    public object? Before { get; init; }
    public object? After { get; init; }

    public string? Reason { get; init; }

    /// <summary>Zone of the record, so the log is searched only within the permitted scope (UC75).</summary>
    public Guid? ZoneId { get; init; }

    /// <summary>
    /// Who acted when it is not the caller of the request, e.g. the recovered account in UC03.
    /// Defaults to the current user; empty for system or background job actions.
    /// </summary>
    public Guid? ActorAccountId { get; init; }
}
