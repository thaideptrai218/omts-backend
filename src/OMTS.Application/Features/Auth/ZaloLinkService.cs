using OMTS.Application.Common.Interfaces.Messaging;
using System.Security.Cryptography;
using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Auth.Dtos;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth;

/// <summary>
/// "Liên kết tài khoản" of the Zalo Mini App, its only way in: no password or verification code on mobile.
/// The Zalo phone number (already verified by Zalo) finds the account the cooperative created and the Zalo user
/// id is kept on it, so the Mini App gets a session again without asking for the number. A trader whose number
/// has no account sends a request instead; the account opens once the Cooperative Owner approves it.
/// </summary>
public sealed class ZaloLinkService(
    IZaloOpenApi zalo,
    IAccountRepository accounts,
    ILoginSessionRepository sessions,
    AuthService authService,
    TraderCustomerLinker customerLinker,
    UsernameGenerator usernames,
    IPasswordHasher passwordHasher,
    INotificationDispatcher notifications,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const int MaxSaveAttempts = 5;

    /// <summary>
    /// Links the account that uses the Zalo phone number and opens a session for it. 404 ZALO_PHONE_NOT_FOUND
    /// (with <c>maskedPhone</c>) creates nothing: the Mini App then offers <see cref="RequestTraderAccessAsync"/>.
    /// </summary>
    public async Task<Result<ZaloLinked>> LinkAsync(ZaloLinkRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var identity = await ReadIdentityAsync(request.ZaloAccessToken, request.PhoneToken, cancellationToken);
        if (!identity.IsSuccess)
        {
            return identity.Error!;
        }

        var (zaloId, phone) = identity.Value;
        var account = await accounts.FindByPhoneAsync(phone, cancellationToken);
        if (account is null)
        {
            return new AppError(ErrorKind.NotFound, UserMessages.ZaloPhoneNotFound, new Dictionary<string, object?>
            {
                ["maskedPhone"] = Masking.Phone(phone),
            });
        }

        if (await accounts.FindByZaloAccountIdAsync(zaloId, cancellationToken) is { } linked && linked.Id != account.Id)
        {
            return AppError.Conflict(UserMessages.ZaloAlreadyLinked);
        }

        if (await RefusalOfAsync(account, cancellationToken, linkingEmployee: true) is { } refused)
        {
            return refused;
        }

        var hidden = await ApplyLinkAsync(account, zaloId, phone, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // Another account took this Zalo user between the check above and this save.
            return AppError.Conflict(UserMessages.ZaloAlreadyLinked);
        }

        var tokens = await authService.CreateSessionAsync(account, request.ClientType, request.DeviceName, client, cancellationToken);
        return ZaloLinked.From(tokens, hidden);
    }

    /// <summary>A session for the account already linked to this Zalo user (Mini App opened again).</summary>
    public async Task<Result<AuthTokens>> LoginAsync(ZaloLoginRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var zaloUser = await zalo.GetUserIdAsync(request.ZaloAccessToken, cancellationToken);
        if (Refusal(zaloUser, UserMessages.ZaloTokenInvalid, ErrorKind.Unauthorized) is { } userError)
        {
            return userError;
        }

        var account = await accounts.FindByZaloAccountIdAsync(zaloUser.Value!, cancellationToken);
        if (account is null)
        {
            return new AppError(ErrorKind.NotFound, UserMessages.ZaloNotLinked);
        }

        if (await RefusalOfAsync(account, cancellationToken) is { } refused)
        {
            return refused;
        }

        // A temporary password issued again after linking (UC72/UC74) still has to be changed on the web.
        if (account.IsTemporaryPasswordExpired(timeProvider.GetUtcNow()))
        {
            return new AppError(ErrorKind.Forbidden, UserMessages.TemporaryPasswordExpired);
        }

        // Concurrent openings of the Mini App conflict on the account's row version; the loser re-reads it.
        for (var attempt = 1; ; attempt++)
        {
            account.RegisterSuccessfulLogin(timeProvider.GetUtcNow());
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxSaveAttempts)
            {
                await unitOfWork.ReloadAsync(account, cancellationToken);
            }
        }

        return await authService.CreateSessionAsync(account, request.ClientType, request.DeviceName, client, cancellationToken);
    }

    /// <summary>
    /// A trader whose Zalo number has no account asks the cooperative for one: a trader account in Pending approval,
    /// already linked to the Zalo user, that cannot log in until the Cooperative Owner approves it. The Owners get an
    /// in-app notification. A refused trader may ask again; asking again while pending changes nothing.
    /// </summary>
    public async Task<Result<TraderRequestSent>> RequestTraderAccessAsync(ZaloTraderRequest request, CancellationToken cancellationToken)
    {
        var name = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > ZaloTraderRequest.MaxDisplayNameLength)
        {
            return AppError.Invalid("Display name");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > ZaloTraderRequest.MaxNoteLength)
        {
            return AppError.Invalid("Note");
        }

        var identity = await ReadIdentityAsync(request.ZaloAccessToken, request.PhoneToken, cancellationToken);
        if (!identity.IsSuccess)
        {
            return identity.Error!;
        }

        var (zaloId, phone) = identity.Value;
        var account = await accounts.FindByPhoneAsync(phone, cancellationToken);
        if (await accounts.FindByZaloAccountIdAsync(zaloId, cancellationToken) is { } linked && linked.Id != account?.Id)
        {
            return AppError.Conflict(UserMessages.ZaloAlreadyLinked);
        }

        var pending = statusCatalog.CodeOf(StatusIds.Account.PendingApproval);
        var now = timeProvider.GetUtcNow();
        switch (account)
        {
            case null:
                account = new Account
                {
                    SubjectType = SubjectType.Trader,
                    StatusId = StatusIds.Account.PendingApproval,
                    Username = await usernames.NewTraderUsernameAsync(cancellationToken),
                    DisplayName = name,
                    PhoneNumber = phone,
                    // Zalo verified the number; it becomes the login number once approved.
                    PhoneVerifiedAt = now,
                    PasswordHash = UnusablePasswordHash(),
                    ZaloAccountId = zaloId,
                    AccessRequestedAt = now,
                    AccessRequestNote = note,
                };
                accounts.Add(account);
                audit.Record(new AuditEntry("Account.AccessRequested", nameof(Account))
                {
                    RecordId = account.Id,
                    RecordCode = account.Username,
                    ActorAccountId = account.Id,
                    After = new { account.Username, account.DisplayName, account.PhoneNumber, Status = pending, ZaloLinked = true },
                });
                break;

            case { StatusId: StatusIds.Account.PendingApproval }:
                return new TraderRequestSent(pending, account.DisplayName, Masking.Phone(phone));

            case { StatusId: StatusIds.Account.Rejected, SubjectType: SubjectType.Trader }:
                account.StatusId = StatusIds.Account.PendingApproval;
                account.DisplayName = name;
                account.ZaloAccountId = zaloId;
                account.AccessRequestedAt = now;
                account.AccessRequestNote = note;
                account.RejectionReason = null;
                audit.RecordStatusChange(
                    nameof(Account), account.Id, account.Username, StatusIds.Account.Rejected, StatusIds.Account.PendingApproval, actorAccountId: account.Id);
                break;

            default:
                return AppError.Conflict(UserMessages.ZaloAccountExists);
        }

        await notifications.NotifyAsync(
            await accounts.ListActiveOwnerIdsAsync(cancellationToken),
            NotificationTypes.TraderAccessRequested,
            "Trader account request",
            $"{name} ({Masking.Phone(phone)}) asks for a trader account from the Zalo Mini App.",
            nameof(Account),
            account.Username,
            isMandatory: false,
            cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel request (double tap) took the number or the Zalo user first.
            return AppError.Conflict(UserMessages.ZaloAlreadyLinked);
        }

        return new TraderRequestSent(pending, name, Masking.Phone(phone));
    }

    /// <summary>
    /// The user removes the link from the Mini App (Zalo rule: users delete their own data). The account stays;
    /// the current session ends, so the next opening of the Mini App is a guest until linked again.
    /// </summary>
    public async Task<Result<Done>> UnlinkAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        if (account.ZaloAccountId is not null)
        {
            account.ZaloAccountId = null;
            audit.Record(new AuditEntry("Account.ZaloUnlinked", nameof(Account)) { RecordId = account.Id, RecordCode = account.Username });
        }

        if (await sessions.GetAsync(sessionId, accountId, cancellationToken) is { } session)
        {
            session.Revoke(timeProvider.GetUtcNow(), "Zalo link removed");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    /// <summary>The Zalo user id and the Vietnamese phone number behind the Mini App's two tokens.</summary>
    private async Task<Result<(string ZaloId, string Phone)>> ReadIdentityAsync(
        string accessToken, string phoneToken, CancellationToken cancellationToken)
    {
        var zaloUser = await zalo.GetUserIdAsync(accessToken, cancellationToken);
        if (Refusal(zaloUser, UserMessages.ZaloTokenInvalid, ErrorKind.Unauthorized) is { } userError)
        {
            return userError;
        }

        var zaloPhone = await zalo.GetPhoneNumberAsync(accessToken, phoneToken, cancellationToken);
        if (Refusal(zaloPhone, UserMessages.ZaloPhoneTokenInvalid, ErrorKind.Validation) is { } phoneError)
        {
            return phoneError;
        }

        // Accounts use Vietnamese numbers only (ACS shared rules), so another number can neither match nor be used.
        if (PhoneNumbers.Normalize(zaloPhone.Value) is not { } phone)
        {
            return new AppError(ErrorKind.Validation, UserMessages.ZaloPhoneUnsupported, new Dictionary<string, object?>
            {
                ["maskedPhone"] = Masking.Phone(zaloPhone.Value),
            });
        }

        return (zaloUser.Value!, phone);
    }

    private async Task<bool> ApplyLinkAsync(Account account, string zaloId, string phone, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var before = new { ZaloLinked = account.ZaloAccountId is not null, account.PhoneVerifiedAt, account.MustChangePassword };

        account.ZaloAccountId = zaloId;
        // Zalo already verified the number (BR-34), so the phone step of ACS-02 is done.
        var phoneNewlyVerified = account.PhoneVerifiedAt is null;
        if (phoneNewlyVerified)
        {
            account.PhoneVerifiedAt = now;
        }

        var hidden = phoneNewlyVerified && account.SubjectType == SubjectType.Trader
            && await customerLinker.ApplyVerifiedPhoneAsync(account, phone, cancellationToken);

        // Mobile has no password: the temporary password of a new staff account is not needed any more.
        if (account.MustChangePassword)
        {
            account.RetireTemporaryPassword(UnusablePasswordHash());
        }

        account.RegisterSuccessfulLogin(now);
        audit.Record(new AuditEntry("Account.ZaloLinked", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            ActorAccountId = account.Id,
            Before = before,
            After = new { ZaloLinked = true, account.PhoneVerifiedAt, account.MustChangePassword, OldDocumentsHidden = hidden },
        });
        return hidden;
    }

    /// <summary>
    /// Why the account cannot get a session: a trader request still waiting or refused (with the Owner's reason),
    /// or the rule of the password login (ACS-02): Active, and staff need an effective zone–role pair.
    /// </summary>
    private async Task<AppError?> RefusalOfAsync(Account account, CancellationToken cancellationToken, bool linkingEmployee = false) => account.StatusId switch
    {
        StatusIds.Account.PendingApproval => new AppError(ErrorKind.Forbidden, UserMessages.ZaloApprovalPending),
        StatusIds.Account.Rejected => new AppError(ErrorKind.Forbidden, UserMessages.ZaloRequestRejected, new Dictionary<string, object?>
        {
            ["reason"] = account.RejectionReason,
        }),
        StatusIds.Account.Active when await authService.HasEffectiveAccessAsync(account, cancellationToken, linkingEmployee) => null,
        _ => new AppError(ErrorKind.Forbidden, UserMessages.AccountUnusable),
    };

    // A password nobody knows: these accounts are reached through Zalo, the web through Forgot password.
    private string UnusablePasswordHash() => passwordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private static AppError? Refusal(ZaloLookup lookup, UserMessage rejected, ErrorKind rejectedKind) => lookup.Outcome switch
    {
        ZaloLookupOutcome.Found when !string.IsNullOrWhiteSpace(lookup.Value) => null,
        ZaloLookupOutcome.Unavailable => new AppError(ErrorKind.ServiceUnavailable, UserMessages.ServiceNotResponding("Zalo")),
        _ => new AppError(rejectedKind, rejected),
    };
}
