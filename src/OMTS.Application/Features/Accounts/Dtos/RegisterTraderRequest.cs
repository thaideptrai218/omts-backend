using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="Channel">Sms (phone number) or Email.</param>
public sealed record RegisterTraderRequest(
    VerificationChannel Channel,
    string Identifier,
    string Password,
    string PasswordConfirmation,
    string VerificationToken);
