using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Verification.Dtos;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Verification;

/// <summary>
/// ACS-06 – verify contact information: one-time 6-digit codes by SMS or email, shared by registration,
/// recovery, password change, profile changes and phone verification after login. Codes and tokens are
/// stored hashed and never returned, logged or audited. It never creates accounts or grants permissions.
/// </summary>
public sealed class VerificationService(
    IVerificationCodeRepository codes,
    VerificationThrottle throttle,
    IVerificationCodeHasher codeHasher,
    ISmsSender smsSender,
    IEmailSender emailSender,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>The hour of the limits on codes (BR-ACCOUNT-09) and of the guards around them.</summary>
    public static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(1);

    /// <summary>New requests per purpose and subject (account, or target before registration) per hour.</summary>
    public const int MaxRequestsPerWindow = 5;

    /// <summary>
    /// Opens a request and sends its code. A newer request replaces the open ones of the same purpose and subject
    /// (another target or delivery method), and every open one of the same purpose and contact, however old and whoever
    /// asked: only the latest code works (BR-ACCOUNT-09). A token already issued stays valid until it is used.
    /// Limits: see <see cref="VerificationThrottle"/>. The request is
    /// saved before its code is sent, so parallel starts cannot all send: a start that finds another fresh
    /// request backs off with 429. When sending fails the request is removed again, so a failed send changes
    /// nothing.
    /// </summary>
    public async Task<Result<VerificationStarted>> StartAsync(
        VerificationPurpose purpose, VerificationChannel channel, string target, Guid? accountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var recentRequests = await codes.GetRecentAsync(purpose, accountId, target, now - BudgetWindow, cancellationToken);
        if (await throttle.CheckStartAsync(recentRequests, purpose, channel, target, now, cancellationToken) is { } limited)
        {
            return limited;
        }

        var code = SecretHashing.NewSixDigitCode();
        var request = new VerificationCode
        {
            AccountId = accountId,
            Purpose = purpose,
            Channel = channel,
            Target = target,
            CreatedAt = now,
            LastSentAt = now,
            SendTimes = [now],
            ExpiresAt = now + VerificationCode.Lifetime,
        };
        request.CodeHash = codeHasher.Hash(request.Id, code);
        codes.Add(request);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var parallel = (await codes.GetRecentAsync(purpose, accountId, target, now - VerificationCode.ResendInterval, cancellationToken))
            .FirstOrDefault(c => c.Id != request.Id && now < c.NextResendAt);
        var refused = parallel is not null
            ? VerificationErrors.WaitBeforeNextCode(parallel.NextResendAt, now, await throttle.CodesLeftAsync(purpose, target, now, cancellationToken))
            : await throttle.CheckSavedSendAsync(purpose, channel, target, now, cancellationToken);
        if (refused is not null || !(await SendAsync(channel, target, code, cancellationToken)).Succeeded)
        {
            codes.Remove(request);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return refused ?? ServiceDown(channel);
        }

        var sameContact = await codes.ListOpenAsync(purpose, target, now, cancellationToken);
        var replaced = recentRequests.Where(c => c.IsOpen).Concat(sameContact).Where(c => c.Id != request.Id).Distinct().ToList();
        await CancelAsync(replaced, now, cancellationToken);
        return await StartedAsync(request, now, cancellationToken);
    }

    /// <summary>
    /// Sends a new code for the same request; the old code stops working and the new one has its own 5 wrong entries
    /// (BR-ACCOUNT-09). The limits of <see cref="VerificationThrottle"/> hold: once they allow a new code, the same request can
    /// have it. The send is recorded before the code goes out, so parallel resends of one request send once. A code cancelled
    /// by its 5th wrong entry is not resent: it answers MSG-ACCOUNT-17 as it does on an entry (ACCOUNT-06.0.E2).
    /// </summary>
    public async Task<Result<VerificationStarted>> ResendAsync(Guid verificationId, Guid? callerAccountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var request = await codes.GetByIdAsync(verificationId, cancellationToken);
        // Recovery requests are never resent here: a new POST api/auth/recovery answers the same way whether
        // or not the identifier exists, so this endpoint cannot tell real recovery ids from random ones.
        if (request is null || request.Purpose == VerificationPurpose.AccountRecovery || !MayUse(request, callerAccountId))
        {
            return VerificationErrors.WrongCode();
        }

        if (request.IsLocked)
        {
            return VerificationErrors.CancelledByWrongEntries();
        }

        if (!request.IsOpen)
        {
            return VerificationErrors.WrongCode();
        }

        if (await throttle.CheckResendAsync(request.Purpose, request.Channel, request.Target, now, cancellationToken) is { } refused)
        {
            return refused;
        }

        var previous = (request.CodeHash, request.FailedAttempts, request.SendCount, request.LastSentAt, request.SendTimes, request.ExpiresAt);
        var code = SecretHashing.NewSixDigitCode();
        request.CodeHash = codeHasher.Hash(request.Id, code);
        request.FailedAttempts = 0;
        request.SendCount++;
        request.LastSentAt = now;
        request.SendTimes = [.. request.SendTimes.Where(sent => sent > now - BudgetWindow), now];
        request.ExpiresAt = now + VerificationCode.Lifetime;
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // A parallel resend (or entry) changed the request first.
            await unitOfWork.ReloadAsync(request, cancellationToken);
            return VerificationErrors.WaitBeforeNextCode(request.NextResendAt, now, await throttle.CodesLeftAsync(request.Purpose, request.Target, now, cancellationToken));
        }

        var overLimit = await throttle.CheckSavedSendAsync(request.Purpose, request.Channel, request.Target, now, cancellationToken);
        if (overLimit is not null || !(await SendAsync(request.Channel, request.Target, code, cancellationToken)).Succeeded)
        {
            // The previous code stays valid until its expiry, unless the request changed meanwhile.
            (request.CodeHash, request.FailedAttempts, request.SendCount, request.LastSentAt, request.SendTimes, request.ExpiresAt) = previous;
            await SaveQuietlyAsync(cancellationToken);
            return overLimit ?? ServiceDown(request.Channel);
        }

        return await StartedAsync(request, now, cancellationToken);
    }

    /// <summary>
    /// Checks the code. A wrong code counts an attempt; the 5th wrong entry cancels it (a new one is needed). A
    /// correct code is used up and yields a single-use token valid 10 minutes. The limit of 3 codes per hour also
    /// caps the guesses, as each code has its own 5 entries: 15 per hour for one purpose and target.
    /// </summary>
    public async Task<Result<VerificationTokenIssued>> VerifyAsync(
        Guid verificationId, string? code, Guid? callerAccountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var request = await codes.GetByIdAsync(verificationId, cancellationToken);
        if (request is null || !MayUse(request, callerAccountId))
        {
            return VerificationErrors.WrongCode();
        }

        if (!request.IsUsable(now))
        {
            return request.IsLocked ? Cancelled(request) : VerificationErrors.WrongCode();
        }

        if (string.IsNullOrWhiteSpace(code) || !codeHasher.Matches(request.Id, code.Trim(), request.CodeHash))
        {
            await CountWrongEntryAsync(request, now, cancellationToken);
            return request.IsLocked ? Cancelled(request) : VerificationErrors.WrongCode();
        }

        var token = SecretHashing.NewToken();
        request.ConsumedAt = now;
        request.TokenHash = SecretHashing.Sha256Hex(token);
        request.TokenExpiresAt = now + VerificationCode.TokenLifetime;
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // A parallel entry changed the request first (a wrong code counted, or the same code used).
            return VerificationErrors.WrongCode();
        }

        return new VerificationTokenIssued(token, request.TokenExpiresAt.Value);
    }

    /// <summary>
    /// Validates a token for the calling use case and marks it used; the caller's save stores that together
    /// with its own change. A token of another purpose, account or target is refused like a wrong code.
    /// </summary>
    /// <param name="accountId">The account the request must belong to (null: no account, i.e. registration).</param>
    /// <param name="anyAccount">Password recovery: the token itself names the account.</param>
    public async Task<Result<VerificationCode>> ConsumeTokenAsync(
        string? token,
        IReadOnlyCollection<VerificationPurpose> purposes,
        Guid? accountId,
        string? expectedTarget,
        CancellationToken cancellationToken,
        bool anyAccount = false)
    {
        var now = timeProvider.GetUtcNow();
        var request = string.IsNullOrWhiteSpace(token) ? null : await codes.GetByTokenHashAsync(SecretHashing.Sha256Hex(token.Trim()), cancellationToken);
        if (request is null
            || !purposes.Contains(request.Purpose)
            || !request.IsTokenUsable(now)
            || (!anyAccount && request.AccountId != accountId)
            || (expectedTarget is not null && request.Target != expectedTarget))
        {
            return VerificationErrors.WrongCode();
        }

        request.TokenConsumedAt = now;
        return request;
    }

    /// <summary>
    /// A change or removal of the phone number or email cancels the earlier codes and tokens (BR-ACCOUNT-09): every request
    /// of the account on that channel, so a code or token on the old contact, or on a number entered before the change, no
    /// longer works. They answer like any cancelled code, MSG-ACCOUNT-16, which does not say why. The caller's save stores this
    /// together with the change, so a parallel entry on one of them makes that save conflict rather than slip through.
    /// </summary>
    public async Task CancelForChangedContactAsync(Guid accountId, VerificationChannel channel, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var request in await codes.ListUsableOfAccountAsync(accountId, channel, now, cancellationToken))
        {
            // The token that proves the new contact is consumed in this same save: it is used, not cancelled.
            if (request.TokenConsumedAt is null)
            {
                request.CancelledAt = now;
            }
        }
    }

    public static AppError ServiceDown(VerificationChannel channel) => VerificationErrors.ServiceDown(channel);

    /// <summary>
    /// The 5th wrong entry cancelled the code: that entry and every later one answer MSG-ACCOUNT-17 (BR-ACCOUNT-09).
    /// Exception: password recovery keeps MSG-ACCOUNT-16, so that a recovery id answers exactly like an id that was
    /// never issued and does not tell whether the identifier has an account (BR-ACCOUNT-07).
    /// </summary>
    private static AppError Cancelled(VerificationCode request) =>
        request.Purpose == VerificationPurpose.AccountRecovery ? VerificationErrors.WrongCode() : VerificationErrors.CancelledByWrongEntries();

    // Registration and recovery run before login; every other request belongs to its account only.
    private static bool MayUse(VerificationCode request, Guid? callerAccountId) =>
        request.Purpose is VerificationPurpose.Registration or VerificationPurpose.AccountRecovery || request.AccountId == callerAccountId;

    private Task<DeliveryResult> SendAsync(VerificationChannel channel, string target, string code, CancellationToken cancellationToken) =>
        channel == VerificationChannel.Sms
            ? smsSender.SendAsync(target, VerificationTexts.Sms(code), cancellationToken)
            : emailSender.SendAsync(target, VerificationTexts.EmailSubject, VerificationTexts.EmailHtml(code), VerificationTexts.EmailText(code), cancellationToken: cancellationToken);

    /// <summary>The replaced requests stop working; a parallel change of one of them is re-applied on its newest version.</summary>
    private async Task CancelAsync(IReadOnlyList<VerificationCode> replaced, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var previous in replaced)
        {
            previous.CancelledAt = now;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            foreach (var previous in replaced)
            {
                await unitOfWork.ReloadAsync(previous, cancellationToken);
                previous.CancelledAt ??= now;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SaveQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // The request changed meanwhile (an entry was counted): it keeps its newest state.
        }
    }

    private async Task<VerificationStarted> StartedAsync(VerificationCode request, DateTimeOffset now, CancellationToken cancellationToken) => new(
        request.Id,
        request.Channel == VerificationChannel.Sms ? Masking.Phone(request.Target) : Masking.Email(request.Target),
        request.ExpiresAt,
        await throttle.CodesLeftAsync(request.Purpose, request.Target, now, cancellationToken),
        request.NextResendAt);

    // Each conflict round saves one entry and a request locks after a few, so this covers any burst the
    // per-IP rate limiter lets through.
    private const int MaxCountRetries = 20;

    /// <summary>
    /// Counts a wrong entry so that parallel entries are never lost: on a rowversion conflict the request is
    /// reloaded and the entry counted again on the newest version, until it is saved or the request is no
    /// longer usable (locked or used up by the parallel entries).
    /// </summary>
    private async Task CountWrongEntryAsync(VerificationCode request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            request.FailedAttempts++;
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ConcurrencyConflictException) when (retry < MaxCountRetries)
            {
                await unitOfWork.ReloadAsync(request, cancellationToken);
                if (!request.IsUsable(now))
                {
                    return;
                }
            }
        }
    }
}
