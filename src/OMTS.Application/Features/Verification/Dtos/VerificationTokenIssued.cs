
namespace OMTS.Application.Features.Verification.Dtos;

/// <summary>Single-use proof that the code was entered correctly, consumed by the calling use case.</summary>
public sealed record VerificationTokenIssued(string VerificationToken, DateTimeOffset ExpiresAt);
