namespace OMTS.Application.Features.Verification.Dtos;

/// <summary>A verification request whose code was sent; the target is masked (ACS-06).</summary>
public sealed record VerificationStarted(
    Guid VerificationId,
    string MaskedTarget,
    DateTimeOffset ExpiresAt,
    int SendsLeft,
    DateTimeOffset NextResendAt);

/// <summary>Single-use proof that the code was entered correctly, consumed by the calling use case.</summary>
public sealed record VerificationTokenIssued(string VerificationToken, DateTimeOffset ExpiresAt);

public sealed record StartVerificationRequest(Domain.Enums.VerificationChannel Channel, string Target);

public sealed record VerifyCodeRequest(string Code);
