using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

public sealed record ProfileView(
    Guid AccountId,
    string Username,
    string DisplayName,
    SubjectType SubjectType,
    string? Email,
    bool EmailVerified,
    string? PhoneNumber,
    bool PhoneVerified,
    IReadOnlyList<ProfileGrantView> Grants,
    string RowVersion);
