using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

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
}
