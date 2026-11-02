
namespace OMTS.Application.Features.Verification.Dtos;

/// <summary>A verification request whose code was sent; the target is masked (ACS-06).</summary>
public sealed record VerificationStarted(
    Guid VerificationId,
    string MaskedTarget,
    DateTimeOffset ExpiresAt,
    int SendsLeft,
    DateTimeOffset NextResendAt);
