using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Verification;

/// <summary>
/// Limits of ACS-06 that hold across requests (NFR-01), checked before any code is sent:
/// <list type="bullet">
/// <item>per subject (the account, or the target before registration) and purpose: one send per 60 seconds,
/// 5 requests and 10 wrong codes per hour, whatever the channel or target;</item>
/// <item>per target across accounts and purposes: 10 sends per hour, so a number or an address cannot be
/// flooded through many (e.g. email-only trader) accounts;</item>
/// <item>system-wide: <see cref="VerificationLimitOptions.DailySmsLimit"/> SMS per 24 hours.</item>
/// </list>
/// </summary>
public sealed class VerificationThrottle(
    IVerificationCodeRepository codes,
    IOptions<VerificationLimitOptions> options,
    ILogger<VerificationThrottle> logger)
{
    public const int MaxSendsPerTargetPerWindow = 10;
    private static readonly TimeSpan SmsCeilingWindow = TimeSpan.FromDays(1);

    /// <summary>Null when a new request may be opened; <paramref name="recent"/> are the subject's requests of the last hour.</summary>
    public async Task<AppError?> CheckStartAsync(
        IReadOnlyList<VerificationCode> recent, VerificationChannel channel, string target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (recent.Sum(c => c.FailedAttempts) >= VerificationService.MaxFailedAttemptsPerWindow)
        {
            return VerificationErrors.TooManyAttempts();
        }

        if (recent.FirstOrDefault(c => now < c.NextResendAt) is { } latest)
        {
            return VerificationErrors.WaitBeforeNewRequest(latest, now);
        }

        if (recent.Count >= VerificationService.MaxRequestsPerWindow)
        {
            return VerificationErrors.OutOfRequests(recent[^1].CreatedAt + VerificationService.BudgetWindow, now);
        }

        return await CheckSendAsync(channel, target, now, cancellationToken);
    }

    /// <summary>Null when one more code may be sent to the target (first send or resend).</summary>
    public Task<AppError?> CheckSendAsync(VerificationChannel channel, string target, DateTimeOffset now, CancellationToken cancellationToken) =>
        CheckCountsAsync(channel, target, now, alreadySaved: 0, cancellationToken);

    /// <summary>
    /// The same limits once this send is saved (counted among the sends): null when it still fits. Parallel
    /// sends that all passed <see cref="CheckSendAsync"/> see each other here, and the ones over the limit
    /// back off without sending.
    /// </summary>
    public Task<AppError?> CheckSavedSendAsync(VerificationChannel channel, string target, DateTimeOffset now, CancellationToken cancellationToken) =>
        CheckCountsAsync(channel, target, now, alreadySaved: 1, cancellationToken);

    private async Task<AppError?> CheckCountsAsync(
        VerificationChannel channel, string target, DateTimeOffset now, int alreadySaved, CancellationToken cancellationToken)
    {
        if (await codes.CountSendsToTargetAsync(target, now - VerificationService.BudgetWindow, cancellationToken) - alreadySaved >= MaxSendsPerTargetPerWindow)
        {
            return VerificationErrors.OutOfRequests(now + VerificationService.BudgetWindow, now);
        }

        if (channel != VerificationChannel.Sms)
        {
            return null;
        }

        var limit = options.Value.DailySmsLimit;
        if (await codes.CountSmsSendsAsync(now - SmsCeilingWindow, cancellationToken) - alreadySaved < limit)
        {
            return null;
        }

        logger.LogCritical("Daily SMS ceiling of {Limit} verification messages reached: SMS codes are refused (MSG04) until the 24-hour count drops", limit);
        return VerificationErrors.ServiceDown(VerificationChannel.Sms);
    }
}
