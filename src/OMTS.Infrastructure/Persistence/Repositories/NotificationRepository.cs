using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class NotificationRepository(OmtsDbContext db) : INotificationRepository
{
    public void Add(Notification notification) => db.Notifications.Add(notification);

    public async Task<(IReadOnlyList<Notification> Items, int Total)> ListForRecipientAsync(
        Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Notifications.AsNoTracking().Where(n => n.RecipientAccountId == accountId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<int> CountUnreadAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Notifications.CountAsync(n => n.RecipientAccountId == accountId && n.ReadAt == null, cancellationToken);

    public Task<Notification?> GetForRecipientAsync(Guid notificationId, Guid accountId, CancellationToken cancellationToken) =>
        db.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientAccountId == accountId, cancellationToken);

    public async Task<IReadOnlyList<Notification>> ListUnreadForRecipientAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.Notifications.Where(n => n.RecipientAccountId == accountId && n.ReadAt == null).ToListAsync(cancellationToken);

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
}
