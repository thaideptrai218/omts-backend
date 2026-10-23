namespace OMTS.Application.Common.Models;

/// <summary>Sliding refresh lifetime and hard session limit for a client type (NFR-01).</summary>
public sealed record SessionLifetime(TimeSpan RefreshTokenLifetime, TimeSpan MaxLifetime);
