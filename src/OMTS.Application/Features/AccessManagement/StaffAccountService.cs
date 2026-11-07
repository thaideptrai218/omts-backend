using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
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
using OMTS.Application.Features.Verification;
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
    IAccessGrantRepository grants,
    AccessGrantService grantService,
    VerificationService verification,
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
        // One query each for the linked employees and the pairs of the whole page.
        var accountIds = items.Select(a => a.Id).ToList();
        var employeeIds = await employees.GetIdsByAccountIdsAsync(accountIds, cancellationToken);
        var pairs = (await grants.ListEffectiveForAccountsAsync(accountIds, cancellationToken)).ToLookup(g => g.AccountId);
        var views = items.Select(a => View(a, employeeIds.TryGetValue(a.Id, out var employeeId) ? employeeId : null, pairs[a.Id])).ToList();
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
            if (employee is null || employee.AccountId is not null || employee.StatusId != StatusIds.Employee.Working)
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

        var account = created.Value!.Account;
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel save took a value between the check and this save (unique index): answer as the check would.
            if (await FindTakenFieldAsync(account.Username, account.PhoneNumber, account.Email, null, cancellationToken) is { } taken)
            {
                return taken;
            }

            throw;
        }

        return created.Value with { Account = await ViewAsync((await accounts.GetByIdAsync(account.Id, cancellationToken))!, cancellationToken) };
    }

    /// <summary>
    /// MSG-ACCOUNT-09 (BR-ACCOUNT-02) naming the first of the username, phone number and email that another account
    /// has, verified or not, with that account's id. Null when none is taken.
    /// </summary>
    public async Task<AppError?> FindTakenFieldAsync(string username, string? phone, string? email, Guid? accountId, CancellationToken cancellationToken)
    {
        if (await accounts.FindByUsernameAsync(username, cancellationToken) is { } sameUsername && sameUsername.Id != accountId)
        {
            return AppError.UsedByAnotherAccount(ErrorKind.Conflict, "Username", MessageCodes.Legacy.Msg03, sameUsername.Id);
        }

        if (phone is not null && await accounts.FindByPhoneAsync(phone, cancellationToken) is { } samePhone && samePhone.Id != accountId)
        {
            return AppError.UsedByAnotherAccount(ErrorKind.Conflict, "Phone number", MessageCodes.Legacy.Msg03, samePhone.Id);
        }

        if (email is not null && await accounts.FindByEmailAsync(email, cancellationToken) is { } sameEmail && sameEmail.Id != accountId)
        {
            return AppError.UsedByAnotherAccount(ErrorKind.Conflict, "Email", MessageCodes.Legacy.Msg03, sameEmail.Id);
        }

        return null;
    }

    /// <summary>
    /// Only Staff accounts are created here and in LAB-01: a request for another subject type (Owner rights)
    /// is refused with 403 and audited. The caller saves the audit row.
    /// </summary>
    public AppError? RefuseOtherSubjectType(SubjectType? requested, string? username)
    {
        if (requested is not { } subjectType || subjectType == SubjectType.Staff)
        {
            return null;
        }

        audit.Record(new AuditEntry("Account.OwnerRightsRefused", nameof(Account))
        {
            // A long name typed in a refused request is cut to 100 characters for the audit log.
            RecordCode = username is { Length: > 100 } longName ? longName[..100] : username,
            After = new { RequestedSubjectType = subjectType.ToString() },
        });
        return AppError.Forbidden;
    }

    /// <summary>
    /// Validates and adds a staff account, with the zone–role grants that were sent if any (ACCOUNT-09 step 6, the pairs
    /// can follow in FARM-05), and the employee link to the unit of work without saving, so LABOR-01 can save employee,
    /// account, grants and link together.
    /// </summary>
    public async Task<Result<StaffAccountWithPassword>> AddStaffAccountAsync(
        CreateStaffAccountRequest request, Employee? employee, CancellationToken cancellationToken)
    {
        if (RefuseOtherSubjectType(request.SubjectType, request.Username) is { } refused)
        {
            return refused;
        }

        var fields = await ValidateFieldsAsync(request.DisplayName, request.Username, request.PhoneNumber, request.Email, null, cancellationToken);
        if (!fields.IsSuccess)
        {
            return fields.Error!;
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

        var granted = await grantService.AddGrantsAsync(account, request.Grants ?? [], cancellationToken);
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

        return new StaffAccountWithPassword(await ViewAsync(account, cancellationToken), temporaryPassword, now + Account.TemporaryPasswordLifetime, MessageCodes.Account23);
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

        if (await rowVersions.CheckAccountEditAsync(account, request.RowVersion, cancellationToken) is { } conflict)
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
            await verification.CancelForChangedContactAsync(account.Id, VerificationChannel.Sms, cancellationToken);
        }

        if (account.Email != email)
        {
            account.Email = email;
            account.EmailVerifiedAt = null;
            await verification.CancelForChangedContactAsync(account.Id, VerificationChannel.Email, cancellationToken);
        }

        audit.Record(new AuditEntry("Account.Updated", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            Before = before,
            After = new { account.DisplayName, account.Username, account.PhoneNumber, account.Email },
        });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel save took a value between the check and this save (unique index): answer as the check would.
            if (await FindTakenFieldAsync(username, phone, email, account.Id, cancellationToken) is { } taken)
            {
                return taken;
            }

            throw;
        }

        return await ViewAsync(account, cancellationToken);
    }

    public Task<Result<StaffAccountView>> LockAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, StatusIds.Account.Active, StatusIds.Account.Locked, request.Reason, cancellationToken);

    public Task<Result<StaffAccountView>> UnlockAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, StatusIds.Account.Locked, StatusIds.Account.Active, request.Reason, cancellationToken);

    /// <summary>
    /// New temporary password (24 h, must be changed at first login) of an Active staff account (ACCOUNT-09.4); a Locked
    /// one answers MSG-GEN-09. Every session ends.
    /// </summary>
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

        if (account.StatusId != StatusIds.Account.Active)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Account", statusCatalog.DisplayNameOf(account.StatusId)));
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
        return new StaffAccountWithPassword(await ViewAsync(account, cancellationToken), temporaryPassword, now + Account.TemporaryPasswordLifetime, MessageCodes.Account23);
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
            return AppError.Conflict(UserMessages.WrongStatus("Account", statusCatalog.DisplayNameOf(account.StatusId)));
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

        if (await FindTakenFieldAsync(normalizedUsername, phone, normalizedEmail, accountId, cancellationToken) is { } taken)
        {
            return taken;
        }

        return (name, normalizedUsername, phone, normalizedEmail);
    }

    private async Task<StaffAccountView> ViewAsync(Account account, CancellationToken cancellationToken) => View(
        account,
        (await employees.GetByAccountIdAsync(account.Id, cancellationToken))?.Id ?? account.Employee?.Id,
        await grants.ListEffectiveForAccountAsync(account.Id, cancellationToken));

    private StaffAccountView View(Account account, Guid? employeeId, IEnumerable<AccessGrant> pairs) => new(
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
        pairs.Select(g => new StaffAccountPairView(g.Id, g.ZoneId, g.Zone?.Name, g.ZoneId is null, g.Role.Code, g.Role.Name)).ToList(),
        AccountEditVersion.Of(account));
}
