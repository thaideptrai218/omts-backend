using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid AccountId,
    string DisplayName,
    SubjectType SubjectType,
    bool MustChangePassword,
    bool PhoneVerificationRequired);
