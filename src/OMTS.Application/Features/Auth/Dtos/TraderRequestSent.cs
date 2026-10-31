namespace OMTS.Application.Features.Auth.Dtos;

/// <summary>The request waits for the Cooperative Owner (status PENDING_APPROVAL); the Mini App shows it.</summary>
public sealed record TraderRequestSent(string Status, string DisplayName, string MaskedPhone);
