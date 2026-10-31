using System.Text.Json;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications.Dtos;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Features.Notifications;

/// <summary>
/// ACCOUNT-08 – the notification list of the caller: newest first with the unread count, mark as read, and open the
/// related record after a new access check (GFR-02, BR-ACCOUNT-12). Only the caller's own list rows are reachable; a
/// notification of another account answers as not found.
/// </summary>
public sealed class NotificationInboxService(
    INotificationRepository notifications,
    IRelatedRecordQuery records,
    PermissionService permissions,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const int MaxPageSize = 100;

    public async Task<NotificationPage> ListAsync(Guid accountId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var (items, total) = await notifications.ListAsync(accountId, unreadOnly, page, pageSize, cancellationToken);
        var unread = await notifications.CountUnreadAsync(accountId, cancellationToken);
        return new NotificationPage(items.Select(View).ToList(), page, pageSize, total, unread);
    }

    /// <summary>ACCOUNT-08 step 3 and 08.1: saves the time it was read, once; answers the notification.</summary>
    public async Task<Result<NotificationView>> MarkReadAsync(Guid accountId, Guid notificationId, CancellationToken cancellationToken)
    {
        var notification = await notifications.GetAsync(accountId, notificationId, cancellationToken);
        if (notification is null)
        {
            return AppError.NotFound;
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = timeProvider.GetUtcNow();
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return View(notification);
    }

    /// <summary>ACCOUNT-08.1 for every unread notification of the list.</summary>
    public async Task<NotificationsMarkedRead> MarkAllReadAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var unread = await notifications.ListUnreadAsync(accountId, cancellationToken);
        if (unread.Count > 0)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var notification in unread)
            {
                notification.ReadAt = now;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new NotificationsMarkedRead(unread.Count, await notifications.CountUnreadAsync(accountId, cancellationToken));
    }

    /// <summary>
    /// ACCOUNT-08 steps 4–5: the related record, when it still exists (else MSG-ACCOUNT-22, 08.0.E1) and the caller may
    /// still open it now (else MSG-GEN-08: e.g. the zone–role pair was revoked since). Changes nothing.
    /// </summary>
    public async Task<Result<OpenedNotificationRecord>> OpenAsync(Guid accountId, Guid notificationId, CancellationToken cancellationToken)
    {
        var notification = await notifications.GetAsync(accountId, notificationId, cancellationToken);
        if (notification is not { RelatedRecordType: { } recordType, RelatedRecordId: { } recordId })
        {
            return AppError.NotFound;
        }

        if (await records.FindAsync(recordType, recordId, cancellationToken) is not { } location)
        {
            var objectName = RelatedRecordTypes.ObjectName(recordType);
            return new AppError(ErrorKind.NotFound, UserMessages.RecordNoLongerExists(objectName, notification.RelatedRecordCode ?? ""));
        }

        var rule = NotificationTypes.Find(notification.Type)?.Open ?? NotificationOpenAccess.OwnerOnly;
        var access = await permissions.GetAccessAsync(accountId, cancellationToken);
        var allowed = access is not null && (rule.Function is { } function
            ? access.Allows(function, rule.Action, PermissionScope.ZoneScoped, location.ZoneId)
            : access.IsCooperativeOwner);
        return allowed
            ? new OpenedNotificationRecord(notification.Id, recordType, recordId, notification.RelatedRecordCode, location.ZoneId)
            : AppError.Forbidden;
    }

    private static NotificationView View(Notification notification) => new(
        notification.Id,
        notification.Type,
        notification.Title,
        notification.Body,
        notification.MessageCode,
        Params(notification.MessageParams),
        notification.IsMandatory,
        notification.RelatedRecordType,
        notification.RelatedRecordId,
        notification.RelatedRecordCode,
        notification.CreatedAt,
        notification.ReadAt);

    private static JsonElement? Params(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A row written by hand or by an older version: the English body is still shown.
            return null;
        }
    }
}
