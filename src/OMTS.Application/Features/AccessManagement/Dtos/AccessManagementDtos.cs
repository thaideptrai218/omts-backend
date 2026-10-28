using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

/// <summary>One zone–role row of a grant form; <see cref="ZoneId"/> null means All zones.</summary>
public sealed record GrantDraft(Guid? ZoneId, Guid RoleId, DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null);

/// <param name="SubjectType">Only Staff accounts can be created; any other value is refused and audited.</param>
public sealed record CreateStaffAccountRequest(
    string DisplayName,
    string Username,
    string PhoneNumber,
    string? Email,
    Guid? EmployeeId,
    IReadOnlyList<GrantDraft> Grants,
    SubjectType? SubjectType = null);

public sealed record UpdateStaffAccountRequest(string DisplayName, string Username, string PhoneNumber, string? Email, string? RowVersion);

public sealed record ReasonRequest(string? Reason);

public sealed record StaffAccountView(
    Guid Id,
    string DisplayName,
    string Username,
    string? PhoneNumber,
    bool PhoneVerified,
    string? Email,
    bool EmailVerified,
    string Status,
    bool MustChangePassword,
    Guid? EmployeeId,
    string RowVersion);

/// <summary>Returned once, never stored in clear, never sent by SMS or email (ACS-09).</summary>
public sealed record StaffAccountWithPassword(StaffAccountView Account, string TemporaryPassword, DateTimeOffset TemporaryPasswordExpiresAt);

public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <param name="State">Effective, Expired, NotYetEffective or Revoked (Expired is derived from EffectiveTo).</param>
public sealed record GrantRow(
    Guid Id,
    Guid? ZoneId,
    string? ZoneName,
    bool AllZones,
    Guid RoleId,
    string RoleName,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string State,
    DateTimeOffset? RevokedAt,
    string? RevokeReason);

public sealed record PermissionPreviewRequest(IReadOnlyList<GrantDraft> Grants);

/// <summary>What the draft rows would allow: per function, the actions and where (All zones or zone ids).</summary>
public sealed record PermissionPreviewItem(string Function, string Name, IReadOnlyList<string> Actions, bool AllZones, IReadOnlyList<Guid> ZoneIds);

public sealed record RolePermissionInput(string FunctionCode, IReadOnlyList<PermissionAction> Actions, DataScope DataScope = DataScope.AssignedZone);

public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<RolePermissionInput> Permissions, string? RowVersion = null);

public sealed record CopyRoleRequest(string Name, string? Description = null);

public sealed record RoleView(
    Guid Id,
    string Name,
    string? Description,
    string Status,
    IReadOnlyList<RolePermissionView> Permissions,
    string RowVersion);

public sealed record RolePermissionView(string FunctionCode, IReadOnlyList<string> Actions, DataScope DataScope);

/// <param name="ConfirmMessage">MSG-GEN-04, shown before saving an edit of a role in use.</param>
/// <param name="ConfirmMessageCode">The code of <paramref name="ConfirmMessage"/>.</param>
public sealed record RoleImpact(
    int AccountCount, int ZoneCount, bool AllZonesGranted, IReadOnlyList<string> Accounts, string ConfirmMessage, string ConfirmMessageCode);

public sealed record FunctionCatalogItem(string Code, string Name, bool CooperativeWide, bool IsDashboardTile, IReadOnlyList<string> SupportedActions);
