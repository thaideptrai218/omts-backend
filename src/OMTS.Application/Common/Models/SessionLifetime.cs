namespace OMTS.Application.Common.Models;

/// <summary>
/// Sliding refresh lifetime and hard session limit for a client type (NFR-01), and how long the refresh token that a
/// rotation replaced is still accepted (<paramref name="RefreshGrace"/>).
/// </summary>
public sealed record SessionLifetime(TimeSpan RefreshTokenLifetime, TimeSpan MaxLifetime, TimeSpan RefreshGrace);
