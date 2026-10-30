using OMTS.Application.Features.Testing.Dtos;
using OMTS.Application.Features.Testing.Options;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Auth.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Testing;

/// <summary>
/// Development role testing uses separate internal personas, reached only from the configured linked
/// Owner's authenticated session. The real account, phone, Zalo link and permissions are never changed.
/// No password, phone or Zalo id is assigned to a persona. Refresh remains the normal session flow.
/// </summary>
public sealed class TestRoleService(
    IOptions<TestRoleOptions> options, ICurrentUser user, IAccountRepository accounts,
    IEmployeeRepository employees, ICustomerRepository customers, ICooperativeRepository cooperatives,
    IRoleRepository roles, IAccessGrantRepository grants, ILoginSessionRepository sessions,
    IPasswordHasher passwordHasher, AuthService auth, AuditLogService audit, TimeProvider clock)
{
    private static readonly string[] AvailableRoles = ["Owner", "ZoneManager", "Employee", "Trader"];

    public async Task<Result<TestRoleAvailability>> AvailabilityAsync(CancellationToken ct)
    {
        var allowed = await AuthorizeAsync(ct);
        if (!allowed.IsSuccess) return allowed.Error!;
        var current = await accounts.GetByIdAsync(user.AccountId!.Value, ct);
        var role = current!.Id == options.Value.OwnerAccountId ? "Owner"
            : AvailableRoles.Skip(1).First(r => current.Username == Username(r));
        return new TestRoleAvailability(role, AvailableRoles);
    }

    public async Task<Result<AuthTokens>> SwitchAsync(TestRoleRequest request, ClientInfo client, CancellationToken ct)
    {
        var allowed = await AuthorizeAsync(ct);
        if (!allowed.IsSuccess) return allowed.Error!;
        if (!AvailableRoles.Contains(request.Role)) return AppError.Invalid("Test role");
        var owner = allowed.Value!;
        var target = owner;
        if (request.Role != "Owner")
        {
            var cooperative = await cooperatives.GetCurrentAsync(ct);
            if (cooperative is null) return AppError.NotFound;
            target = await accounts.FindByUsernameAsync(Username(request.Role), ct)
                ?? CreatePersona(owner, request.Role);
            var expectedType = request.Role == "Trader" ? SubjectType.Trader : SubjectType.Staff;
            if (target.StatusId != StatusIds.Account.Active || target.SubjectType != expectedType ||
                target.PhoneNumber is not null || target.ZaloAccountId is not null) return AppError.Forbidden;
            if (request.Role == "Trader")
            {
                if (await customers.GetByAccountIdAsync(target.Id, ct) is null)
                    customers.Add(new Customer { CooperativeId = cooperative.Id, AccountId = target.Id,
                        Name = target.DisplayName });
            }
            else
            {
                if (await employees.GetByAccountIdAsync(target.Id, ct) is null)
                    employees.Add(new Employee { CooperativeId = cooperative.Id, AccountId = target.Id,
                        FullName = target.DisplayName });
                if (request.Role == "ZoneManager") await EnsureZoneGrantAsync(target, owner.Id, ct);
            }
        }
        if (user.SessionId is not { } sessionId ||
            await sessions.GetAsync(sessionId, user.AccountId!.Value, ct) is not { } oldSession) return AppError.Forbidden;
        oldSession.Revoke(clock.GetUtcNow(), "Development test role switch");
        audit.Record(new AuditEntry("Development.TestRoleSwitched", nameof(Account))
        {
            RecordId = target.Id, Before = new { AccountId = user.AccountId },
            After = new { AccountId = target.Id, request.Role },
            Reason = "Explicit role testing by the configured linked Owner",
        });
        // Normal CreateSession persists the persona, grant, profile, audit and revoked old session together.
        return await auth.CreateSessionAsync(target, ClientType.Mobile, "Zalo · thử vai trò", client, ct);
    }

    private async Task<Result<Account>> AuthorizeAsync(CancellationToken ct)
    {
        var config = options.Value;
        if (!config.Enabled || config.OwnerAccountId == Guid.Empty) return AppError.NotFound;
        if (user.AccountId is null || user.SessionId is null) return AppError.Forbidden;
        var owner = await accounts.GetByIdAsync(config.OwnerAccountId, ct);
        if (owner is not { SubjectType: SubjectType.CooperativeOwner, StatusId: StatusIds.Account.Active } ||
            string.IsNullOrEmpty(owner.ZaloAccountId) || owner.PhoneVerifiedAt is null) return AppError.Forbidden;
        if (user.AccountId == owner.Id) return owner;
        foreach (var role in AvailableRoles.Skip(1))
        {
            var persona = await accounts.FindByUsernameAsync(Username(role), ct);
            if (persona?.Id == user.AccountId) return owner;
        }
        return AppError.Forbidden;
    }

    private Account CreatePersona(Account owner, string role)
    {
        var name = role switch { "Trader" => "Thương lái thử", "ZoneManager" => "Quản lý vùng thử", _ => "Nhân viên thử" };
        var account = new Account
        {
            Username = Username(role), DisplayName = name + " · " + owner.DisplayName[..Math.Min(owner.DisplayName.Length, 160)],
            SubjectType = role == "Trader" ? SubjectType.Trader : SubjectType.Staff,
            PasswordHash = passwordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))),
            // Identity was verified by the linked Owner; the internal persona has no public login identifier.
            PhoneVerifiedAt = owner.PhoneVerifiedAt,
        };
        accounts.Add(account);
        return account;
    }

    private async Task EnsureZoneGrantAsync(Account account, Guid ownerId, CancellationToken ct)
    {
        var name = "Dev Zone Manager " + ownerId.ToString("N");
        var role = await roles.FindByNameAsync(name, ct);
        if (role is null)
        {
            role = new Role { Name = name, Description = "Development test persona; not assigned to real staff." };
            foreach (var p in DefaultRoles.ZoneManager.Permissions)
            {
                var actions = DefaultRoles.ActionsOf(p);
                role.Permissions.Add(new RolePermission { FunctionCode = p.FunctionCode,
                    CanView = actions.Contains(OMTS.Application.Features.Authorization.Models.PermissionAction.View),
                    CanCreate = actions.Contains(OMTS.Application.Features.Authorization.Models.PermissionAction.Create),
                    CanEdit = actions.Contains(OMTS.Application.Features.Authorization.Models.PermissionAction.Edit),
                    CanDelete = actions.Contains(OMTS.Application.Features.Authorization.Models.PermissionAction.Delete),
                    OtherPermissions = actions.Where(a => a is not (OMTS.Application.Features.Authorization.Models.PermissionAction.View
                        or OMTS.Application.Features.Authorization.Models.PermissionAction.Create
                        or OMTS.Application.Features.Authorization.Models.PermissionAction.Edit
                        or OMTS.Application.Features.Authorization.Models.PermissionAction.Delete)).Select(a => a.ToString().ToUpperInvariant()).ToList() });
            }
            roles.Add(role);
        }
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);
        if (!await grants.HasEffectiveGrantAsync(account.Id, today, ct))
            grants.Add(new AccessGrant { AccountId = account.Id, RoleId = role.Id, ScopeType = AccessScopeType.AllZones,
                EffectiveFrom = today, GrantedByAccountId = ownerId });
    }

    private string Username(string role) => TestRoleNames.Username(options.Value.OwnerAccountId, role);
}
