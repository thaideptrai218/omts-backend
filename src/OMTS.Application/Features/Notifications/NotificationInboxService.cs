using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Notifications.Dtos;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Features.Notifications;

/// <summary>The caller's in-app notifications (web and Zalo Mini App): list, mark one or all as read.</summary>
public sealed class NotificationInboxService(INotificationRepository notifications, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    public const int MaxPageSize = 100;

    public async Task<Result<NotificationPage>> ListAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            return AppError.Invalid("Page");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            return AppError.Invalid("Page size");
        }

        var (items, total) = await notifications.ListForRecipientAsync(accountId, page, pageSize, cancellationToken);
        var unread = await notifications.CountUnreadAsync(accountId, cancellationToken);
        return new NotificationPage(items.Select(ToView).ToList(), total, unread);
    }

    /// <summary>Reading one again keeps the first read time; another account's notification is not found.</summary>
    public async Task<Result<Done>> MarkReadAsync(Guid accountId, Guid notificationId, CancellationToken cancellationToken)
    {
        var notification = await notifications.GetForRecipientAsync(notificationId, accountId, cancellationToken);
        if (notification is null)
        {
            return AppError.NotFound;
        }

        notification.ReadAt ??= timeProvider.GetUtcNow();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    public async Task MarkAllReadAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var notification in await notifications.ListUnreadForRecipientAsync(accountId, cancellationToken))
        {
            notification.ReadAt = now;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static NotificationView ToView(Notification n) =>
        new(n.Id, n.Type, n.Title, n.Body, n.RelatedRecordType, n.RelatedRecordCode, n.CreatedAt, n.ReadAt, n.IsMandatory);
}
