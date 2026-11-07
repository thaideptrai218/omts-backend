using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Verification;

/// <summary>Answers of ACCOUNT-06 (MSG-ACCOUNT-16/17/18, MSG-ACCOUNT-13).</summary>
internal static class VerificationErrors
{
    public static AppError WrongCode() => new(ErrorKind.Validation, UserMessages.WrongVerificationCode);

    /// <summary>MSG-ACCOUNT-17: the 5th wrong entry cancelled this code; a new one is needed.</summary>
    public static AppError CancelledByWrongEntries() => new(ErrorKind.Validation, UserMessages.TooManyWrongCodes);

    /// <summary>MSG-ACCOUNT-13 (GFR-06): the code could not be sent; nothing is changed.</summary>
    public static AppError ServiceDown(VerificationChannel channel) =>
        new(ErrorKind.ServiceUnavailable, UserMessages.CouldNotSend("verification code", channel == VerificationChannel.Sms ? "SMS" : "email"));

    /// <summary>MSG-ACCOUNT-18 (429) with the real wait until a new code can be sent.</summary>
    /// <param name="codesLeft">Codes the purpose and target can still get this hour, when the refusal comes from that limit.</param>
    public static AppError WaitBeforeNextCode(DateTimeOffset nextAllowed, DateTimeOffset now, int? codesLeft = null)
    {
        var wait = Math.Max(1, (int)Math.Ceiling((nextAllowed - now).TotalSeconds));
        var details = new Dictionary<string, object?> { ["retryAfterSeconds"] = wait };
        if (codesLeft is { } left)
        {
            details["sendsLeft"] = left;
        }

        return new AppError(ErrorKind.TooManyRequests, UserMessages.TooManyCodeRequests(wait), details);
    }
}
