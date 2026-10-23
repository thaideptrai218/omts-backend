using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Verification;

/// <summary>Answers of ACS-06 that carry a wait or a limit.</summary>
internal static class VerificationErrors
{
    public static AppError WrongCode() => new(ErrorKind.Validation, UserMessages.WrongVerificationCode);

    public static AppError TooManyAttempts() => new(ErrorKind.Locked, UserMessages.TooManyAttempts);

    public static AppError ServiceDown(VerificationChannel channel) =>
        new(ErrorKind.ServiceUnavailable, UserMessages.ServiceNotResponding(channel == VerificationChannel.Sms ? "SMS" : "Email"));

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
