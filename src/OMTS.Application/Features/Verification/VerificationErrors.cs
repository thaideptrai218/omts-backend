using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Verification;

/// <summary>Answers of ACS-06 that carry a wait or a limit (MSG-ACCOUNT-16/17/18, MSG-ACCOUNT-13).</summary>
internal static class VerificationErrors
{
    public static AppError WrongCode() => new(ErrorKind.Validation, UserMessages.WrongVerificationCode);

    /// <summary>MSG-ACCOUNT-17: the code was entered wrongly too many times and is cancelled.</summary>
    public static AppError TooManyAttempts() => new(ErrorKind.Locked, UserMessages.TooManyWrongCodes);

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
