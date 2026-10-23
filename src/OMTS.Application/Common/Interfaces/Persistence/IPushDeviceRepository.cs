using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IPushDeviceRepository
{
    Task<PushDevice?> GetByTokenAsync(string token, CancellationToken cancellationToken);

    Task<IReadOnlyList<PushDevice>> ListForAccountAsync(Guid accountId, CancellationToken cancellationToken);

    void Add(PushDevice device);

    /// <summary>Devices are technical records: unregistering removes the row.</summary>
    void Remove(PushDevice device);
}
