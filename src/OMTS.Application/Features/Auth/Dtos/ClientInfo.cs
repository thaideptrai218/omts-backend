namespace OMTS.Application.Features.Auth.Dtos;

/// <summary>Request metadata stored on the session for the user's device list.</summary>
public sealed record ClientInfo(string? UserAgent, string? IpAddress);
