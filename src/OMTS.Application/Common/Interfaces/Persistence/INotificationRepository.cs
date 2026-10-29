using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface INotificationRepository
{
    void Add(Notification notification);

    /// <summary>A page of the account's in-app notifications, newest first, and the total count.</summary>
    Task<(IReadOnlyList<Notification> Items, int Total)> ListForRecipientAsync(
        Guid accountId, int page, int pageSize, CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>The notification if it belongs to <paramref name="accountId"/>.</summary>
    Task<Notification?> GetForRecipientAsync(Guid notificationId, Guid accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Notification>> ListUnreadForRecipientAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Notification preferences of the given accounts that still exist.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationPreference>>> GetPreferencesAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}
