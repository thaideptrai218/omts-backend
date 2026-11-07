namespace OMTS.Application.Features.Authorization.Dtos;

/// <summary>Response of GET /me/permissions, used to build web/mobile menus.</summary>
public sealed record MyPermissions(
    Guid AccountId,
    string SubjectType,
    bool IsCooperativeOwner,
    bool RequiresPasswordChange,
    bool RequiresPhoneVerification,
    IReadOnlyList<FunctionAccess> Functions,
    IReadOnlyList<GrantView> Grants);
