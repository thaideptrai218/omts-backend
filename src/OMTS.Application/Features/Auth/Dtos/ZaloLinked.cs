using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

/// <summary>
/// The session of a freshly linked account: the fields of <see cref="AuthTokens"/>, plus MSG57 in
/// <see cref="Notice"/> when several old customer profiles share the trader's number (BR-34).
/// </summary>
public sealed record ZaloLinked(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid AccountId,
    string DisplayName,
    SubjectType SubjectType,
    bool MustChangePassword,
    bool PhoneVerificationRequired,
    bool OldDocumentsHidden,
    string? Notice)
{
    public static ZaloLinked From(AuthTokens tokens, bool oldDocumentsHidden) => new(
        tokens.AccessToken,
        tokens.AccessTokenExpiresAt,
        tokens.RefreshToken,
        tokens.RefreshTokenExpiresAt,
        tokens.AccountId,
        tokens.DisplayName,
        tokens.SubjectType,
        tokens.MustChangePassword,
        tokens.PhoneVerificationRequired,
        oldDocumentsHidden,
        oldDocumentsHidden ? UserMessages.OldDocumentsHidden.Text : null);
}
