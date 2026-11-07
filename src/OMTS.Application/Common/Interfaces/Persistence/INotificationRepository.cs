using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// Notification rows and the push preferences they are dispatched with. The list methods only see rows of the channel
/// InApp, the notifications of the list (ACCOUNT-08); SMS and Zalo OA sendings are never listed (BR-SALE-09).
/// </summary>
public interface INotificationRepository
{
    void Add(Notification notification);

    /// <summary>Notification preferences of the given accounts that are Active (others are not notified).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationPreference>>> GetPreferencesAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>One page of the account's list, newest first (untracked), and how many rows match.</summary>
    Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(
        Guid recipientAccountId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken);

    /// <summary>A notification of the account's list, tracked; null for a row of another account.</summary>
    Task<Notification?> GetAsync(Guid recipientAccountId, Guid notificationId, CancellationToken cancellationToken);

    /// <summary>Every unread notification of the account's list, tracked.</summary>
    Task<IReadOnlyList<Notification>> ListUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken);
}
