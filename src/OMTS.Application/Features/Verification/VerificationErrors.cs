using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Verification;

/// <summary>Answers of ACS-06 that carry a wait or a limit (MSG-ACCOUNT-16/17/18, MSG-ACCOUNT-13).</summary>
internal static class VerificationErrors
{
    public static AppError WrongCode() => new(ErrorKind.Validation, UserMessages.WrongVerificationCode);

    /// <summary>MSG-ACCOUNT-17 (423): the wrong codes of the last hour leave no attempt, even for a right code.</summary>
    public static AppError TooManyAttempts() => new(ErrorKind.Locked, UserMessages.TooManyWrongCodes);

    /// <summary>MSG-ACCOUNT-17 (400, MSG16 before): the 5th wrong entry cancelled this code; a new one is needed.</summary>
    public static AppError CancelledByWrongEntries() =>
        new(ErrorKind.Validation, UserMessages.TooManyWrongCodes with { LegacyCode = MessageCodes.Legacy.Msg16 });

    /// <summary>
    /// MSG-ACCOUNT-18 (423, MSG69 before): no new request after 10 wrong codes in the hour, until the oldest request
    /// with a wrong code leaves the hour.
    /// </summary>
    public static AppError WrongCodeBudgetSpent(DateTimeOffset nextAllowed, DateTimeOffset now)
    {
        var wait = Seconds(nextAllowed, now);
        return new AppError(
            ErrorKind.Locked,
            UserMessages.TooManyCodeRequests(wait) with { LegacyCode = MessageCodes.Legacy.Msg69 },
            new Dictionary<string, object?> { ["retryAfterSeconds"] = wait });
    }

    /// <summary>MSG-ACCOUNT-13 (GFR-06): the code could not be sent; nothing is changed.</summary>
    public static AppError ServiceDown(VerificationChannel channel) =>
        new(ErrorKind.ServiceUnavailable, UserMessages.CouldNotSend("verification code", channel == VerificationChannel.Sms ? "SMS" : "email"));

    public static AppError OutOfRequests(DateTimeOffset nextAllowed, DateTimeOffset now) => Wait(nextAllowed, now);

    /// <summary>A new request while the latest one was sent less than 60 seconds ago: wait until it may be resent.</summary>
    public static AppError WaitBeforeNewRequest(VerificationCode latest, DateTimeOffset now) => Wait(latest.NextResendAt, now);

    /// <summary>A resend of the same request: wait for the next send, or none is left (0, start again).</summary>
    public static AppError TooSoon(VerificationCode request, DateTimeOffset now)
    {
        var wait = request.SendsLeft == 0 ? 0 : Seconds(request.NextResendAt, now);
        return new AppError(
            ErrorKind.TooManyRequests,
            UserMessages.TooManyCodeRequests(wait),
            new Dictionary<string, object?> { ["retryAfterSeconds"] = wait, ["sendsLeft"] = request.SendsLeft });
    }

    private static AppError Wait(DateTimeOffset nextAllowed, DateTimeOffset now)
    {
        var wait = Seconds(nextAllowed, now);
        return new AppError(ErrorKind.TooManyRequests, UserMessages.TooManyCodeRequests(wait), new Dictionary<string, object?> { ["retryAfterSeconds"] = wait });
    }

    private static int Seconds(DateTimeOffset until, DateTimeOffset now) => Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
}
