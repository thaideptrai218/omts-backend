using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Verification;

/// <summary>"Verification" section: system-wide limits on code delivery.</summary>
public sealed class VerificationLimitOptions
{
    public const string SectionName = "Verification";

    /// <summary>
    /// SMS codes (first sends and resends) the whole system may send in 24 hours. Beyond it SMS codes answer
    /// MSG-ACCOUNT-13 and a critical log entry alerts the operator (cost control against SMS pumping).
    /// </summary>
    public int DailySmsLimit { get; set; } = 500;
}

/// <summary>
/// Limits of ACCOUNT-06, checked before any code is sent. The rule of BR-ACCOUNT-09:
/// <list type="bullet">
/// <item>one purpose and one phone number or email: at most 3 codes in any hour, at least 60 seconds apart, whoever
/// asks and whichever request carries them.</item>
/// </list>
/// Guards against abuse that the rule does not cover (NFR-01); a normal user does not reach them:
/// <list type="bullet">
/// <item>per subject (the account, or the target before registration) and purpose: one new request per 60 seconds and
/// 5 per hour, whatever the channel or target;</item>
/// <item>per target across accounts and purposes: 10 sends per hour;</item>
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
        IReadOnlyList<VerificationCode> recent, VerificationPurpose purpose, VerificationChannel channel, string target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await CheckCodeLimitAsync(purpose, target, now, alreadySaved: 0, cancellationToken) is { } limited)
        {
            return limited;
        }

        if (recent.FirstOrDefault(c => now < c.NextResendAt) is { } latest)
        {
            return VerificationErrors.WaitBeforeNextCode(latest.NextResendAt, now, await CodesLeftAsync(purpose, target, now, cancellationToken));
        }

        if (recent.Count >= VerificationService.MaxRequestsPerWindow)
        {
            return VerificationErrors.WaitBeforeNextCode(recent[^1].CreatedAt + VerificationService.BudgetWindow, now, await CodesLeftAsync(purpose, target, now, cancellationToken));
        }

        return await CheckCountsAsync(purpose, channel, target, now, alreadySaved: 0, cancellationToken);
    }

    /// <summary>Null when one more code of this request may be sent.</summary>
    public async Task<AppError?> CheckResendAsync(
        VerificationPurpose purpose, VerificationChannel channel, string target, DateTimeOffset now, CancellationToken cancellationToken) =>
        await CheckCodeLimitAsync(purpose, target, now, alreadySaved: 0, cancellationToken)
            ?? await CheckCountsAsync(purpose, channel, target, now, alreadySaved: 0, cancellationToken);

    /// <summary>
    /// The same limits once this send is saved (counted among the sends): null when it still fits. Parallel
    /// sends that all passed the first check see each other here, and the ones over the limit back off without
    /// sending.
    /// </summary>
    public async Task<AppError?> CheckSavedSendAsync(
        VerificationPurpose purpose, VerificationChannel channel, string target, DateTimeOffset now, CancellationToken cancellationToken) =>
        await CheckCodeLimitAsync(purpose, target, now, alreadySaved: 1, cancellationToken)
            ?? await CheckCountsAsync(purpose, channel, target, now, alreadySaved: 1, cancellationToken);

    /// <summary>Codes this purpose and target can still get in the current hour.</summary>
    public async Task<int> CodesLeftAsync(VerificationPurpose purpose, string target, DateTimeOffset now, CancellationToken cancellationToken) =>
        CodesLeft((await SendTimesAsync(purpose, target, now, cancellationToken)).Count);

    /// <summary>BR-ACCOUNT-09: the wait until the code limit of the hour or the 60 seconds between two codes allows a new one.</summary>
    /// <param name="alreadySaved">1 when the send being checked is already among the saved ones.</param>
    private async Task<AppError?> CheckCodeLimitAsync(
        VerificationPurpose purpose, string target, DateTimeOffset now, int alreadySaved, CancellationToken cancellationToken)
    {
        var earlier = (await SendTimesAsync(purpose, target, now, cancellationToken)).ToList();
        if (alreadySaved == 1 && earlier.LastIndexOf(now) is >= 0 and var own)
        {
            earlier.RemoveAt(own);
        }

        var left = CodesLeft(earlier.Count);
        if (left == 0)
        {
            // The hour counts from the oldest send that has to leave for a new code to fit.
            var oldest = earlier[earlier.Count - VerificationCode.MaxCodesPerHour];
            return VerificationErrors.WaitBeforeNextCode(oldest + VerificationService.BudgetWindow, now, left);
        }

        return earlier.Count > 0 && earlier[^1] + VerificationCode.ResendInterval > now
            ? VerificationErrors.WaitBeforeNextCode(earlier[^1] + VerificationCode.ResendInterval, now, left)
            : null;
    }

    private Task<IReadOnlyList<DateTimeOffset>> SendTimesAsync(VerificationPurpose purpose, string target, DateTimeOffset now, CancellationToken cancellationToken) =>
        codes.GetSendTimesAsync(purpose, target, now - VerificationService.BudgetWindow, cancellationToken);

    private static int CodesLeft(int sent) => Math.Max(0, VerificationCode.MaxCodesPerHour - sent);

    private async Task<AppError?> CheckCountsAsync(
        VerificationPurpose purpose, VerificationChannel channel, string target, DateTimeOffset now, int alreadySaved, CancellationToken cancellationToken)
    {
        // Newest last, so the send being checked is at the end: the hour counts from the oldest send that has to leave.
        var sends = await codes.GetSendTimesToTargetAsync(target, now - VerificationService.BudgetWindow, cancellationToken);
        if (sends.Count - alreadySaved >= MaxSendsPerTargetPerWindow)
        {
            var oldest = sends[sends.Count - alreadySaved - MaxSendsPerTargetPerWindow];
            var codesLeft = CodesLeft((await SendTimesAsync(purpose, target, now, cancellationToken)).Count - alreadySaved);
            return VerificationErrors.WaitBeforeNextCode(oldest + VerificationService.BudgetWindow, now, codesLeft);
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

        logger.LogCritical("Daily SMS ceiling of {Limit} verification messages reached: SMS codes are refused (MSG-ACCOUNT-13) until the 24-hour count drops", limit);
        return VerificationErrors.ServiceDown(VerificationChannel.Sms);
    }
}
