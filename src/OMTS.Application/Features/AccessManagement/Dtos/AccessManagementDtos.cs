using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

/// <summary>
/// One zone–role row of a grant form (FARM-05): the fixed role by <see cref="RoleId"/> or by <see cref="Role"/> code
/// (ZONE_MANAGER or ACCOUNTANT; both may be sent if they name the same role), and a zone or All zones
/// (<see cref="ZoneId"/> null). A pair is Effective from its grant until it is revoked.
/// </summary>
public sealed record GrantDraft(Guid? ZoneId, Guid? RoleId = null, string? Role = null);

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

/// <summary>A zone–role pair of the Zone access screen (SCR-FARM-04): effective and revoked pairs with their dates.</summary>
/// <param name="Role">Code of the fixed role: ZONE_MANAGER or ACCOUNTANT.</param>
/// <param name="State">Effective or Revoked.</param>
/// <param name="GrantedAt">When the Owner granted the pair.</param>
public sealed record GrantRow(
    Guid Id,
    Guid? ZoneId,
    string? ZoneName,
    bool AllZones,
    Guid RoleId,
    string Role,
    string RoleName,
    string State,
    DateTimeOffset GrantedAt,
    Guid GrantedByAccountId,
    string? GrantedByName,
    DateTimeOffset? RevokedAt,
    Guid? RevokedByAccountId,
    string? RevokedByName,
    string? RevokeReason);

public sealed record PermissionPreviewRequest(IReadOnlyList<GrantDraft> Grants);

/// <summary>A fixed role (BR-FARM-07) with its permissions of the §I.4.4 matrix; read only.</summary>
/// <param name="Code">ZONE_MANAGER or ACCOUNTANT.</param>
public sealed record RoleView(Guid Id, string Code, string Name, string? Description, IReadOnlyList<RolePermissionView> Permissions);

public sealed record RolePermissionView(string FunctionCode, IReadOnlyList<string> Actions);

public sealed record FunctionCatalogItem(string Code, string Name, bool CooperativeWide, bool IsDashboardTile, IReadOnlyList<string> SupportedActions);
