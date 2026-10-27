using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Common;

namespace OMTS.Application.Features.Audit;

/// <summary>
/// Discontinue / reactivate of records that name their own audit action (FARM-02 zones, FARM-04 rafts), done the
/// same way everywhere. Other modules record the generic <c>{type}.StatusChanged</c> row with
/// <see cref="AuditLogService.RecordStatusChange"/>.
/// </summary>
public sealed class StatusChangeService(IStatusCatalog statusCatalog, RowVersionGuard rowVersions, AuditLogService audit, IUnitOfWork unitOfWork)
{
    private const int MaxReasonLength = 1000;

    /// <summary>
    /// Checks the optional reason (MSG01 "Reason" beyond 1000 characters) and the row version the screen was read
    /// from (MSG06), sets the new status and saves it with one audit row: <paramref name="entry"/> plus the old and
    /// new status codes and the reason. Null when saved.
    /// </summary>
    public async Task<AppError?> ChangeAsync<T>(
        T record, int newStatusId, AuditEntry entry, string? reason, string? rowVersion, CancellationToken cancellationToken)
        where T : AuditableEntity, IHasStatus
    {
        reason = reason?.Trim();
        if (reason?.Length > MaxReasonLength)
        {
            return AppError.Invalid("Reason");
        }

        if (await rowVersions.CheckAsync(record, rowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var oldStatusId = record.StatusId;
        record.StatusId = newStatusId;
        audit.Record(entry with
        {
            Before = new { Status = statusCatalog.CodeOf(oldStatusId) },
            After = new { Status = statusCatalog.CodeOf(newStatusId) },
            Reason = string.IsNullOrEmpty(reason) ? null : reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return null;
    }

    /// <summary>MSG61: the record's current status does not allow the change to <paramref name="newStatusId"/>.</summary>
    public AppError WrongStatus(string entityName, IHasStatus record, int newStatusId, string reason) => AppError.Conflict(UserMessages.CannotChangeStatus(
        entityName, statusCatalog.DisplayNameOf(record.StatusId), statusCatalog.DisplayNameOf(newStatusId), reason));
}
