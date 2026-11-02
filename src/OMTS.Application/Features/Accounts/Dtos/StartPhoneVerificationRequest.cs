using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="PhoneNumber">Required for traders; staff may omit it to confirm the number the Owner entered.</param>
public sealed record StartPhoneVerificationRequest(string? PhoneNumber);
