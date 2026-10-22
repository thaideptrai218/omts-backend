using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
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
    /// (another target or delivery method). Limits per subject, whatever the channel or target: one send per
    /// 60 seconds, 5 requests and 10 wrong codes per hour. Nothing is saved when sending fails.
    /// </summary>
    public async Task<Result<VerificationStarted>> StartAsync(
        VerificationPurpose purpose, VerificationChannel channel, string target, Guid? accountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var recentRequests = await codes.GetRecentAsync(purpose, accountId, target, now - BudgetWindow, cancellationToken);
        if (recentRequests.Sum(c => c.FailedAttempts) >= MaxFailedAttemptsPerWindow)
        {
            return TooManyAttempts();
        }

        if (recentRequests.FirstOrDefault(c => now < c.NextResendAt) is { } recent)
        {
            return TooSoon(recent, now);
        }

        if (recentRequests.Count >= MaxRequestsPerWindow)
        {
            return OutOfRequests(recentRequests[^1].CreatedAt + BudgetWindow, now);
        }

        var open = recentRequests.Where(c => c.IsOpen).ToList();

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
        request.CodeHash = HashCode(request.Id, code);

        if (!(await SendAsync(channel, target, code, cancellationToken)).Succeeded)
        {
            return ServiceDown(channel);
        }

        foreach (var previous in open)
        {
            previous.CancelledAt = now;
        }

        codes.Add(request);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Started(request);
    }

    /// <summary>Sends a new code for the same request; the old code stops working. At most 3 sends, 60 s apart.</summary>
    public async Task<Result<VerificationStarted>> ResendAsync(Guid verificationId, Guid? callerAccountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var request = await codes.GetByIdAsync(verificationId, cancellationToken);
        // Recovery requests are never resent here: a new POST api/auth/recovery answers the same way whether
        // or not the identifier exists, so this endpoint cannot tell real recovery ids from random ones.
        if (request is null || request.Purpose == VerificationPurpose.AccountRecovery
            || !MayUse(request, callerAccountId) || !request.IsOpen || request.IsLocked)
        {
            return WrongCode();
        }

        if (!request.CanResend(now))
        {
            return TooSoon(request, now);
        }

        var code = SecretHashing.NewSixDigitCode();
        if (!(await SendAsync(request.Channel, request.Target, code, cancellationToken)).Succeeded)
        {
            // The current code stays valid until its expiry.
            return ServiceDown(request.Channel);
        }

        request.CodeHash = HashCode(request.Id, code);
        request.SendCount++;
        request.LastSentAt = now;
        request.ExpiresAt = now + VerificationCode.Lifetime;
        await unitOfWork.SaveChangesAsync(cancellationToken);
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
            return WrongCode();
        }

        var recentRequests = await codes.GetRecentAsync(request.Purpose, request.AccountId, request.Target, now - BudgetWindow, cancellationToken);
        if (recentRequests.Sum(c => c.FailedAttempts) >= MaxFailedAttemptsPerWindow)
        {
            return TooManyAttempts();
        }

        if (string.IsNullOrWhiteSpace(code) || HashCode(request.Id, code.Trim()) != request.CodeHash)
        {
            await CountWrongEntryAsync(request, now, cancellationToken);
            return WrongCode();
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
            return WrongCode();
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
            return WrongCode();
        }

        request.TokenConsumedAt = now;
        return request;
    }

    // Registration and recovery run before login; every other request belongs to its account only.
    private static bool MayUse(VerificationCode request, Guid? callerAccountId) =>
        request.Purpose is VerificationPurpose.Registration or VerificationPurpose.AccountRecovery || request.AccountId == callerAccountId;

    private Task<DeliveryResult> SendAsync(VerificationChannel channel, string target, string code, CancellationToken cancellationToken) =>
        channel == VerificationChannel.Sms
            ? smsSender.SendAsync(target, VerificationTexts.Sms(code), cancellationToken)
            : emailSender.SendAsync(target, VerificationTexts.EmailSubject, VerificationTexts.EmailHtml(code), VerificationTexts.EmailText(code), cancellationToken: cancellationToken);

    private static string HashCode(Guid requestId, string code) => SecretHashing.Sha256Hex($"{requestId:N}:{code}");

    private static VerificationStarted Started(VerificationCode request) => new(
        request.Id,
        request.Channel == VerificationChannel.Sms ? Masking.Phone(request.Target) : Masking.Email(request.Target),
        request.ExpiresAt,
        request.SendsLeft,
        request.NextResendAt);

    private static AppError WrongCode() => new(ErrorKind.Validation, UserMessages.WrongVerificationCode);

    // Each conflict round saves one entry and a request locks after a few, so this covers any burst the
    // per-IP rate limiter lets through.
    private const int MaxCountRetries = 20;

    private static AppError TooManyAttempts() => new(ErrorKind.Locked, UserMessages.TooManyAttempts);

    private static AppError OutOfRequests(DateTimeOffset nextAllowed, DateTimeOffset now)
    {
        var wait = Math.Max(1, (int)Math.Ceiling((nextAllowed - now).TotalSeconds));
        return new AppError(ErrorKind.TooManyRequests, UserMessages.TooManyCodeRequests(wait), new Dictionary<string, object?> { ["retryAfterSeconds"] = wait });
    }

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

    public static AppError ServiceDown(VerificationChannel channel) =>
        new(ErrorKind.ServiceUnavailable, UserMessages.ServiceNotResponding(channel == VerificationChannel.Sms ? "SMS" : "Email"));

    private static AppError TooSoon(VerificationCode request, DateTimeOffset now)
    {
        var wait = request.SendsLeft == 0 ? 0 : Math.Max(1, (int)Math.Ceiling((request.NextResendAt - now).TotalSeconds));
        return new AppError(
            ErrorKind.TooManyRequests,
            UserMessages.TooManyCodeRequests(wait),
            new Dictionary<string, object?> { ["retryAfterSeconds"] = wait, ["sendsLeft"] = request.SendsLeft });
    }
}
