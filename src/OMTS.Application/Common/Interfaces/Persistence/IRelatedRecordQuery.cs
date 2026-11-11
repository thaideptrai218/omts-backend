namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Where a related record of a notification is now; <see cref="ZoneId"/> is null for a record with no single zone.</summary>
/// <param name="ZoneId">The zone of the record.</param>
/// <param name="TraderAccountId">The trader account a record of a customer profile belongs to now (§I.4.4 †).</param>
public sealed record RelatedRecordLocation(Guid? ZoneId, Guid? TraderAccountId = null, Guid? EmployeeAccountId = null,
    Guid? ResponsibleAccountId = null, IReadOnlyCollection<Guid>? EmployeeAccountIds = null);

/// <summary>
/// Finds the record a notification (ACCOUNT-08 step 5) or an audit event (ACCOUNT-10.1) points to, whatever its status.
/// </summary>
public interface IRelatedRecordQuery
{
    /// <summary>The record, or null when it no longer exists or its type is unknown (MSG-ACCOUNT-22).</summary>
    /// <param name="recordType">An entity name, or a type of <c>RelatedRecordTypes</c>.</param>
    Task<RelatedRecordLocation?> FindAsync(string recordType, Guid recordId, CancellationToken cancellationToken);
}
