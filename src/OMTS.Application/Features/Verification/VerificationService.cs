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
    /// <summary>Window of the limits that hold across requests and delivery methods of one subject.</summary>
    public static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(1);

    /// <summary>New requests per purpose and subject (account, or target before registration) per hour.</summary>
    public const int MaxRequestsPerWindow = 5;

    /// <summary>Wrong codes per purpose and subject per hour, over all its requests (blocks code guessing).</summary>
    public const int MaxFailedAttemptsPerWindow = 10;

    /// <summary>
    /// Opens a request and sends its code. A newer request replaces the open ones of the same purpose
    /// (another target or delivery method). Limits: see <see cref="VerificationThrottle"/>. The request is
    /// saved before its code is sent, so parallel starts of one subject cannot all send: a start that finds
    /// another fresh request backs off with 429. When sending fails the request is removed again, so a failed
    /// send changes nothing.
    /// </summary>
    public async Task<Result<VerificationStarted>> StartAsync(
        VerificationPurpose purpose, VerificationChannel channel, string target, Guid? accountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var recentRequests = await codes.GetRecentAsync(purpose, accountId, target, now - BudgetWindow, cancellationToken);
        if (await throttle.CheckStartAsync(recentRequests, channel, target, now, cancellationToken) is { } refused)
        {
            return refused;
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
            ExpiresAt = now + VerificationCode.Lifetime,
        };
        request.CodeHash = codeHasher.Hash(request.Id, code);
        codes.Add(request);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var parallel = (await codes.GetRecentAsync(purpose, accountId, target, now - VerificationCode.ResendInterval, cancellationToken))
            .FirstOrDefault(c => c.Id != request.Id);
        if (parallel is not null || !(await SendAsync(channel, target, code, cancellationToken)).Succeeded)
        {
            codes.Remove(request);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return parallel is not null ? VerificationErrors.WaitBeforeNewRequest(parallel, now) : ServiceDown(channel);
        }

        await CancelAsync(recentRequests.Where(c => c.IsOpen).ToList(), now, cancellationToken);
        return Started(request);
    }

    /// <summary>
    /// Sends a new code for the same request; the old code stops working. At most 3 sends, 60 s apart. The
    /// send is recorded before the code goes out, so parallel resends of one request send once.
    /// </summary>
    public async Task<Result<VerificationStarted>> ResendAsync(Guid verificationId, Guid? callerAccountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var request = await codes.GetByIdAsync(verificationId, cancellationToken);
        // Recovery requests are never resent here: a new POST api/auth/recovery answers the same way whether
        // or not the identifier exists, so this endpoint cannot tell real recovery ids from random ones.
        if (request is null || request.Purpose == VerificationPurpose.AccountRecovery
            || !MayUse(request, callerAccountId) || !request.IsOpen || request.IsLocked)
        {
            return VerificationErrors.WrongCode();
        }

        if (!request.CanResend(now))
        {
            return VerificationErrors.TooSoon(request, now);
        }

        if (await throttle.CheckSendAsync(request.Channel, request.Target, now, cancellationToken) is { } refused)
        {
            return refused;
        }

        var previous = (request.CodeHash, request.SendCount, request.LastSentAt, request.ExpiresAt);
        var code = SecretHashing.NewSixDigitCode();
        request.CodeHash = codeHasher.Hash(request.Id, code);
        request.SendCount++;
        request.LastSentAt = now;
        request.ExpiresAt = now + VerificationCode.Lifetime;
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // A parallel resend (or entry) changed the request first.
            await unitOfWork.ReloadAsync(request, cancellationToken);
            return VerificationErrors.TooSoon(request, now);
        }

        if (!(await SendAsync(request.Channel, request.Target, code, cancellationToken)).Succeeded)
        {
            // The previous code stays valid until its expiry, unless the request changed meanwhile.
            (request.CodeHash, request.SendCount, request.LastSentAt, request.ExpiresAt) = previous;
            await SaveQuietlyAsync(cancellationToken);
            return ServiceDown(request.Channel);
        }

        return Started(request);
    }

    /// <summary>
    /// Checks the code. A wrong code counts an attempt; the 5th wrong entry locks the request (a new one is
    /// needed). A correct code is used up and yields a single-use token valid 10 minutes.
    /// </summary>
    public async Task<Result<VerificationTokenIssued>> VerifyAsync(
        Guid verificationId, string? code, Guid? callerAccountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var request = await codes.GetByIdAsync(verificationId, cancellationToken);
        if (request is null || !MayUse(request, callerAccountId) || !request.IsUsable(now))
        {
            return VerificationErrors.WrongCode();
        }

        var recentRequests = await codes.GetRecentAsync(request.Purpose, request.AccountId, request.Target, now - BudgetWindow, cancellationToken);
        if (recentRequests.Sum(c => c.FailedAttempts) >= MaxFailedAttemptsPerWindow)
        {
            return VerificationErrors.TooManyAttempts();
        }

        if (string.IsNullOrWhiteSpace(code) || !codeHasher.Matches(request.Id, code.Trim(), request.CodeHash))
        {
            await CountWrongEntryAsync(request, now, cancellationToken);
            return VerificationErrors.WrongCode();
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

    public static AppError ServiceDown(VerificationChannel channel) => VerificationErrors.ServiceDown(channel);

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

    private static VerificationStarted Started(VerificationCode request) => new(
        request.Id,
        request.Channel == VerificationChannel.Sms ? Masking.Phone(request.Target) : Masking.Email(request.Target),
        request.ExpiresAt,
        request.SendsLeft,
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
