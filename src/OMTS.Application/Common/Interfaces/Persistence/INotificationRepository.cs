using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// Notification rows. The list methods only see rows of the channel
/// InApp, the notifications of the list (ACCOUNT-08); SMS and Zalo OA sendings are never listed (BR-SALE-09).
/// </summary>
public interface INotificationRepository
{
    void Add(Notification notification);

    /// <summary>The given accounts that are Active (others are not notified).</summary>
    Task<IReadOnlySet<Guid>> GetActiveAccountIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>One page of the account's list, newest first (untracked), and how many rows match.</summary>
    Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(
        Guid recipientAccountId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken);

    /// <summary>How many different records got a notification of <paramref name="type"/> in the list since <paramref name="since"/> (one transaction notifies every Owner, so the owners count once).</summary>
    Task<int> CountRecordsNotifiedSinceAsync(string type, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>True when a notification of <paramref name="type"/> was made in the list since <paramref name="since"/>.</summary>
    Task<bool> AnyOfTypeSinceAsync(string type, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>A notification of the account's list, tracked; null for a row of another account.</summary>
    Task<Notification?> GetAsync(Guid recipientAccountId, Guid notificationId, CancellationToken cancellationToken);

    /// <summary>Every unread notification of the account's list, tracked.</summary>
    Task<IReadOnlyList<Notification>> ListUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken);
}
