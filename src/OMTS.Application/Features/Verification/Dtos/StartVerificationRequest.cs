
namespace OMTS.Application.Features.Verification.Dtos;

public sealed record StartVerificationRequest(Domain.Enums.VerificationChannel Channel, string Target);
