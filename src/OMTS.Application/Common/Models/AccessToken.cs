namespace OMTS.Application.Common.Models;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);
