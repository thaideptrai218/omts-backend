using OMTS.Application.Common.Messages;

namespace OMTS.Application.Common.Results;

/// <summary>What kind of failure a use case reports; the API maps it to an HTTP status.</summary>
public enum ErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Locked,
    TooManyRequests,
    ServiceUnavailable,
    BadGateway,

    /// <summary>The server could not complete the operation (e.g. generating an export file).</summary>
    Internal,
}

/// <summary>
/// A business failure with its SRS message. <see cref="Details"/> become extra fields of the problem
/// response (e.g. <c>field</c>, <c>existingId</c>) so clients can open the right record.
/// </summary>
public sealed record AppError(ErrorKind Kind, UserMessage Message, IReadOnlyDictionary<string, object?>? Details = null)
{
    /// <summary>MSG-GEN-01 naming the invalid field.</summary>
    public static AppError Invalid(string field, string? hint = null) => new(
        ErrorKind.Validation,
        UserMessages.Invalid(field),
        hint is null ? new Dictionary<string, object?> { ["field"] = field } : new Dictionary<string, object?> { ["field"] = field, ["hint"] = hint });

    /// <summary>MSG-GEN-01 naming the first invalid field; <c>fields</c> lists every invalid one.</summary>
    public static AppError InvalidFields(IReadOnlyList<string> fields) => new(
        ErrorKind.Validation,
        UserMessages.Invalid(fields[0]),
        new Dictionary<string, object?> { ["field"] = fields[0], ["fields"] = fields });

    public static readonly AppError Forbidden = new(ErrorKind.Forbidden, UserMessages.NoPermission);

    public static readonly AppError NotFound = new(ErrorKind.NotFound, UserMessages.NotFound);

    /// <summary>MSG-GEN-02 with the id of the record that already exists.</summary>
    public static AppError AlreadyExists(string item, Guid existingId) =>
        new(ErrorKind.Conflict, UserMessages.AlreadyExists(item), new Dictionary<string, object?> { ["existingId"] = existingId });

    /// <summary>MSG-ACCOUNT-09 naming the field (lower case in the text, as entered in <c>field</c>).</summary>
    /// <param name="field">e.g. "Phone number": the <c>field</c> extension, as in MSG-GEN-01.</param>
    /// <param name="legacyCode">The code the same refusal had before (MSG03 or MSG01).</param>
    /// <param name="existingId">The other account, when the caller may open it (staff accounts).</param>
    public static AppError UsedByAnotherAccount(ErrorKind kind, string field, string legacyCode, Guid? existingId = null)
    {
        var details = new Dictionary<string, object?> { ["field"] = field };
        if (existingId is { } id)
        {
            details["existingId"] = id;
        }

        return new AppError(kind, UserMessages.UsedByAnotherAccount(field.ToLowerInvariant(), legacyCode), details);
    }

    public static AppError Conflict(UserMessage message, IReadOnlyDictionary<string, object?>? details = null) =>
        new(ErrorKind.Conflict, message, details);
}
