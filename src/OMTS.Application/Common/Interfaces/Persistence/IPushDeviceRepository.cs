using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IPushDeviceRepository
{
    Task<PushDevice?> GetByTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Devices that receive push now: notifications allowed and the app still logged in with the registering
    /// session (not revoked, and its refresh token, which never outlives the absolute expiry, not expired).
    /// </summary>
    Task<IReadOnlyList<PushDevice>> ListDeliverableAsync(IReadOnlyCollection<Guid> accountIds, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(PushDevice device);

    /// <summary>Devices are technical records: unregistering removes the row.</summary>
    void Remove(PushDevice device);
}
