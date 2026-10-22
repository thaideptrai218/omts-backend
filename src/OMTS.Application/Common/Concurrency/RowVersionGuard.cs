using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Messages;
using OMTS.Domain.Common;

namespace OMTS.Application.Common.Concurrency;

/// <summary>
/// Optimistic concurrency for edits (NFR-09): the client sends back the <c>rowVersion</c> it read; a newer
/// version on the server blocks the save with MSG06 naming who saved last and when.
/// </summary>
public sealed class RowVersionGuard(IAccountRepository accounts, IUnitOfWork unitOfWork)
{
    /// <summary>Null when the client edited the current version (the save is then also checked by the database).</summary>
    public async Task<AppError?> CheckAsync(AuditableEntity entity, string? rowVersion, CancellationToken cancellationToken)
    {
        if (!TryDecode(rowVersion, out var expected))
        {
            return AppError.Invalid("RowVersion");
        }

        if (!entity.RowVersion.AsSpan().SequenceEqual(expected))
        {
            return await ConflictAsync(entity, cancellationToken);
        }

        unitOfWork.ExpectRowVersion(entity, expected);
        return null;
    }

    /// <summary>MSG06 for an entity someone else changed.</summary>
    public async Task<AppError> ConflictAsync(AuditableEntity entity, CancellationToken cancellationToken)
    {
        var editorId = entity.UpdatedBy ?? entity.CreatedBy;
        var editor = editorId is { } id ? await accounts.GetByIdAsync(id, cancellationToken) : null;
        var time = BusinessClock.Format(entity.UpdatedAt ?? entity.CreatedAt);
        return AppError.Conflict(
            UserMessages.ConcurrentUpdate(editor?.DisplayName ?? "another user", time),
            new Dictionary<string, object?> { ["currentRowVersion"] = Encode(entity.RowVersion) });
    }

    public static string Encode(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    private static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
