namespace OMTS.Application.Features.AccessManagement.Dtos;

/// <summary>A trader's request for an account, as the Cooperative Owner reviews it (web and Mini App).</summary>
/// <param name="Status">PENDING_APPROVAL, REJECTED or, right after approval, ACTIVE.</param>
public sealed record TraderRequestView(
    Guid AccountId,
    string DisplayName,
    string? PhoneNumber,
    string? Note,
    DateTimeOffset? RequestedAt,
    string Status,
    string? RejectionReason);
