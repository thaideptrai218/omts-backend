using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface INotificationRepository
{
    void Add(Notification notification);

    /// <summary>Notification preferences of the given accounts that still exist.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationPreference>>> GetPreferencesAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}
