using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement;

/// <summary>
/// ACS-09 – the Cooperative Owner creates and edits staff accounts, locks/unlocks them and issues
/// temporary passwords; there is no delete. Owner and Trader accounts can never be managed here.
/// </summary>
public sealed class StaffAccountService(
    IAccountRepository accounts,
    IEmployeeRepository employees,
    ILoginSessionRepository sessions,
    AccessGrantService grantService,
    IPasswordHasher passwordHasher,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const int MaxDisplayNameLength = 200;
    public const int MaxReasonLength = 1000;

    public async Task<Paged<StaffAccountView>> SearchAsync(string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));
        var (items, total) = await accounts.SearchStaffAsync(search, statusId, page, pageSize, cancellationToken);
        // One query for the linked employees of the whole page.
        var employeeIds = await employees.GetIdsByAccountIdsAsync(items.Select(a => a.Id).ToList(), cancellationToken);
        var views = items.Select(a => View(a, employeeIds.TryGetValue(a.Id, out var employeeId) ? employeeId : null)).ToList();
        return new Paged<StaffAccountView>(views, page, pageSize, total);
    }

    public async Task<Result<StaffAccountView>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await accounts.GetByIdAsync(id, cancellationToken) is { SubjectType: SubjectType.Staff } account
            ? await ViewAsync(account, cancellationToken)
            : AppError.NotFound;

    public async Task<Result<StaffAccountWithPassword>> CreateAsync(CreateStaffAccountRequest request, CancellationToken cancellationToken)
    {
        Employee? employee = null;
        if (request.EmployeeId is { } employeeId)
        {
            employee = await employees.GetByIdAsync(employeeId, cancellationToken);
            if (employee is null || employee.AccountId is not null)
            {
                return AppError.Invalid("Employee");
            }
        }

        var created = await AddStaffAccountAsync(request, employee, cancellationToken);
        if (!created.IsSuccess)
        {
            // A refused attempt to create an Owner account is still audited.
            if (created.Error!.Kind == ErrorKind.Forbidden)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return created.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return created;
    }

    /// <summary>
    /// Validates and adds a staff account with its zone–role grants (and the employee link) to the unit of
    /// work without saving, so LAB-01 can save employee, account, grants and link together.
    /// </summary>
    public async Task<Result<StaffAccountWithPassword>> AddStaffAccountAsync(
        CreateStaffAccountRequest request, Employee? employee, CancellationToken cancellationToken)
    {
        if (request.SubjectType is { } subjectType && subjectType != SubjectType.Staff)
        {
            audit.Record(new AuditEntry("Account.OwnerRightsRefused", nameof(Account))
            {
                RecordCode = request.Username is { Length: > 100 } longName ? longName[..100] : request.Username,
                After = new { RequestedSubjectType = subjectType.ToString() },
            });
            return AppError.Forbidden;
        }

        var fields = await ValidateFieldsAsync(request.DisplayName, request.Username, request.PhoneNumber, request.Email, null, cancellationToken);
        if (!fields.IsSuccess)
        {
            return fields.Error!;
        }

        if (request.Grants is not { Count: > 0 })
        {
            return AppError.Invalid("Zone-role pair");
        }

        var (name, username, phone, email) = fields.Value!;
        var now = timeProvider.GetUtcNow();
        var temporaryPassword = TemporaryPasswords.New();
        var account = new Account
        {
            SubjectType = SubjectType.Staff,
            StatusId = StatusIds.Account.Active,
            DisplayName = name,
            Username = username,
            PhoneNumber = phone,
            Email = email,
            PasswordHash = passwordHasher.Hash(temporaryPassword),
            MustChangePassword = true,
            TemporaryPasswordIssuedAt = now,
        };
        accounts.Add(account);
        if (employee is not null)
        {
            employee.AccountId = account.Id;
        }

        var granted = await grantService.AddGrantsAsync(account, request.Grants, cancellationToken);
        if (!granted.IsSuccess)
        {
            return granted.Error!;
        }

        audit.Record(new AuditEntry("Account.Created", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            After = new { account.DisplayName, account.Username, account.PhoneNumber, account.Email, account.SubjectType, EmployeeId = employee?.Id, account.MustChangePassword },
        });

        return new StaffAccountWithPassword(await ViewAsync(account, cancellationToken), temporaryPassword, now + Account.TemporaryPasswordLifetime);
    }

    public async Task<Result<StaffAccountView>> UpdateAsync(Guid id, UpdateStaffAccountRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(id, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        if (account.SubjectType != SubjectType.Staff)
        {
            return AppError.Forbidden;
        }

        var fields = await ValidateFieldsAsync(request.DisplayName, request.Username, request.PhoneNumber, request.Email, account.Id, cancellationToken);
        if (!fields.IsSuccess)
        {
            return fields.Error!;
        }

        if (await rowVersions.CheckAsync(account, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var (name, username, phone, email) = fields.Value!;
        var before = new { account.DisplayName, account.Username, account.PhoneNumber, account.Email };
        account.DisplayName = name;
        account.Username = username;
        if (account.PhoneNumber != phone)
        {
            // A new number must be verified again by the holder (at next login).
            account.PhoneNumber = phone;
            account.PhoneVerifiedAt = null;
        }

        if (account.Email != email)
        {
            account.Email = email;
            account.EmailVerifiedAt = null;
        }

        audit.Record(new AuditEntry("Account.Updated", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            Before = before,
            After = new { account.DisplayName, account.Username, account.PhoneNumber, account.Email },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(account, cancellationToken);
    }

    public Task<Result<StaffAccountView>> LockAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, StatusIds.Account.Active, StatusIds.Account.Locked, request.Reason, cancellationToken);

    public Task<Result<StaffAccountView>> UnlockAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, StatusIds.Account.Locked, StatusIds.Account.Active, request.Reason, cancellationToken);

    /// <summary>New temporary password (24 h, must be changed at first login); every session ends.</summary>
    public async Task<Result<StaffAccountWithPassword>> ResetPasswordAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(id, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        if (account.SubjectType != SubjectType.Staff)
        {
            return AppError.Forbidden;
        }

        var now = timeProvider.GetUtcNow();
        var temporaryPassword = TemporaryPasswords.New();
        account.PasswordHash = passwordHasher.Hash(temporaryPassword);
        account.MustChangePassword = true;
        account.TemporaryPasswordIssuedAt = now;
        // A fresh temporary password also lifts a temporary login lock.
        account.FailedLoginCount = 0;
        account.LockoutEndAt = null;
        await sessions.RevokeAllAsync(account.Id, now, "Temporary password issued", cancellationToken);
        audit.Record(new AuditEntry("Account.PasswordReset", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            After = new { account.MustChangePassword, account.TemporaryPasswordIssuedAt, SessionsRevoked = true },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new StaffAccountWithPassword(await ViewAsync(account, cancellationToken), temporaryPassword, now + Account.TemporaryPasswordLifetime);
    }

    private async Task<Result<StaffAccountView>> ChangeStatusAsync(Guid id, int from, int to, string? reason, CancellationToken cancellationToken)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
        {
            return AppError.Invalid("Reason");
        }

        var account = await accounts.GetByIdAsync(id, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        // The Owner account cannot be locked; trader accounts are handled by the System Administrator only.
        if (account.SubjectType != SubjectType.Staff)
        {
            return AppError.Forbidden;
        }

        if (account.StatusId != from)
        {
            var current = statusCatalog.DisplayNameOf(account.StatusId);
            return AppError.Conflict(UserMessages.CannotChangeStatus(
                "Account", current, statusCatalog.DisplayNameOf(to), $"the account is already {current.ToLowerInvariant()}"));
        }

        account.StatusId = to;
        if (to == StatusIds.Account.Locked)
        {
            await sessions.RevokeAllAsync(account.Id, timeProvider.GetUtcNow(), "Account locked", cancellationToken);
        }

        audit.RecordStatusChange(nameof(Account), account.Id, account.Username, from, to, reason);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(account, cancellationToken);
    }

    private async Task<Result<(string Name, string Username, string Phone, string? Email)>> ValidateFieldsAsync(
        string? displayName, string? username, string? phoneNumber, string? email, Guid? accountId, CancellationToken cancellationToken)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxDisplayNameLength)
        {
            return AppError.Invalid("Display name");
        }

        var normalizedUsername = UsernameGenerator.Normalize(username);
        if (normalizedUsername is null)
        {
            return AppError.Invalid("Username");
        }

        var phone = PhoneNumbers.Normalize(phoneNumber);
        if (phone is null)
        {
            return AppError.Invalid("Phone number");
        }

        string? normalizedEmail = null;
        if (!string.IsNullOrWhiteSpace(email) && (normalizedEmail = EmailAddresses.Normalize(email)) is null)
        {
            return AppError.Invalid("Email");
        }

        if (await accounts.FindByUsernameAsync(normalizedUsername, cancellationToken) is { } sameUsername && sameUsername.Id != accountId)
        {
            return AppError.AlreadyExists("Username", sameUsername.Id);
        }

        if (await accounts.FindByPhoneAsync(phone, cancellationToken) is { } samePhone && samePhone.Id != accountId)
        {
            return AppError.AlreadyExists("Phone number", samePhone.Id);
        }

        return (name, normalizedUsername, phone, normalizedEmail);
    }

    private async Task<StaffAccountView> ViewAsync(Account account, CancellationToken cancellationToken) =>
        View(account, (await employees.GetByAccountIdAsync(account.Id, cancellationToken))?.Id ?? account.Employee?.Id);

    private StaffAccountView View(Account account, Guid? employeeId) => new(
        account.Id,
        account.DisplayName,
        account.Username,
        account.PhoneNumber,
        account.PhoneVerifiedAt is not null,
        account.Email,
        account.EmailVerifiedAt is not null,
        statusCatalog.DisplayNameOf(account.StatusId),
        account.MustChangePassword,
        employeeId,
        RowVersionGuard.Encode(account.RowVersion));
}
