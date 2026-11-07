using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Seed;

/// <summary>Reads the seeded catalog from <see cref="StatusCatalogSeed"/>; the database holds the same rows.</summary>
internal sealed class StatusCatalog : IStatusCatalog
{
    private static readonly Dictionary<int, Status> ById = StatusCatalogSeed.Statuses.ToDictionary(s => s.Id);

    public string CodeOf(int statusId) => Get(statusId).Code;

    public string DisplayNameOf(int statusId) => Get(statusId).DisplayName;

    private static Status Get(int statusId) =>
        ById.TryGetValue(statusId, out var status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(statusId), statusId, "Unknown status id.");
}
