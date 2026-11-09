namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>A session whose revocation is to be published to the store.</summary>
public readonly record struct RevokedSession(Guid SessionId, DateTimeOffset RevokedAt);
