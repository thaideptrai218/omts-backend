using OMTS.Application.Common.Messages;

namespace OMTS.Application.Common.Results;

/// <summary>
/// A business failure with its SRS message. <see cref="Details"/> become extra fields of the problem
/// response (e.g. <c>field</c>, <c>existingId</c>) so clients can open the right record.
/// </summary>
public sealed record AppError(ErrorKind Kind, UserMessage Message, IReadOnlyDictionary<string, object?>? Details = null)
{
    /// <summary>MSG01 naming the invalid field.</summary>
    public static AppError Invalid(string field, string? hint = null) => new(
        ErrorKind.Validation,
        UserMessages.Invalid(field),
        hint is null ? new Dictionary<string, object?> { ["field"] = field } : new Dictionary<string, object?> { ["field"] = field, ["hint"] = hint });

    /// <summary>MSG01 naming the first invalid field; <c>fields</c> lists every invalid one.</summary>
    public static AppError InvalidFields(IReadOnlyList<string> fields) => new(
        ErrorKind.Validation,
        UserMessages.Invalid(fields[0]),
        new Dictionary<string, object?> { ["field"] = fields[0], ["fields"] = fields });

    public static readonly AppError Forbidden = new(ErrorKind.Forbidden, UserMessages.NoPermission);

    public static readonly AppError NotFound = new(ErrorKind.NotFound, UserMessages.NotFound);

    /// <summary>MSG03 with the id of the record that already exists.</summary>
    public static AppError AlreadyExists(string item, Guid existingId) =>
        new(ErrorKind.Conflict, UserMessages.AlreadyExists(item), new Dictionary<string, object?> { ["existingId"] = existingId });

    public static AppError Conflict(UserMessage message, IReadOnlyDictionary<string, object?>? details = null) =>
        new(ErrorKind.Conflict, message, details);
}
