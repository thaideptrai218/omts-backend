using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="CurrentPassword">Required: a stolen session alone must not be able to take over the account's contacts.</param>
public sealed record StartContactChangeRequest(VerificationChannel Channel, string Target, string? CurrentPassword = null);
