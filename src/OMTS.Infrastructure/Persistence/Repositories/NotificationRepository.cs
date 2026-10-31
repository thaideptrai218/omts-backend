using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class NotificationRepository(OmtsDbContext db) : INotificationRepository
{
    public void Add(Notification notification) => db.Notifications.Add(notification);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationPreference>>> GetPreferencesAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        // Only active accounts are notified; only their preference column is read.
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id) && a.StatusId == StatusIds.Account.Active)
            .Select(a => new { a.Id, a.NotificationPreferences })
            .ToListAsync(cancellationToken);

        return accounts.ToDictionary(a => a.Id, a => (IReadOnlyList<NotificationPreference>)a.NotificationPreferences);
    }

    public async Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(
        Guid recipientAccountId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = ListOf(recipientAccountId).AsNoTracking();
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<int> CountUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken) =>
        ListOf(recipientAccountId).CountAsync(n => n.ReadAt == null, cancellationToken);

    public Task<Notification?> GetAsync(Guid recipientAccountId, Guid notificationId, CancellationToken cancellationToken) =>
        ListOf(recipientAccountId).FirstOrDefaultAsync(n => n.Id == notificationId, cancellationToken);

    public async Task<IReadOnlyList<Notification>> ListUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken) =>
        await ListOf(recipientAccountId).Where(n => n.ReadAt == null).ToListAsync(cancellationToken);

    // The notification list of one account: never the SMS / Zalo OA sendings (BR-SALE-09).
    private IQueryable<Notification> ListOf(Guid recipientAccountId) =>
        db.Notifications.Where(n => n.RecipientAccountId == recipientAccountId && n.Channel == NotificationChannel.InApp);
}
