using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ICooperativeRepository
{
    /// <summary>The single cooperative of the deployment (created by the bootstrap command), or null before it.</summary>
    Task<Cooperative?> GetCurrentAsync(CancellationToken cancellationToken);
}
