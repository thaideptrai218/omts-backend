using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Messages;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

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
        // An empty version was never issued for a saved row (SQL Server always has one): invalid input, not a
        // stale screen. (The in-memory test provider issues empty versions, which still match.)
        if (!TryDecode(rowVersion, out var expected) || (expected.Length == 0 && entity.RowVersion.Length > 0))
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

    /// <summary>
    /// The same check for account forms (profile, staff account), against <see cref="AccountEditVersion"/>:
    /// logins write the account row, so its row version would make every open form stale. A login between
    /// this read and the save still fails that save (the database checks the row version read here).
    /// </summary>
    public async Task<AppError?> CheckAccountEditAsync(Account account, string? editVersion, CancellationToken cancellationToken)
    {
        if (editVersion is null)
        {
            return AppError.Invalid("RowVersion");
        }

        return editVersion == AccountEditVersion.Of(account)
            ? null
            : await ConflictAsync(account, cancellationToken, AccountEditVersion.Of(account));
    }

    /// <summary>MSG06 for an entity someone else changed.</summary>
    public async Task<AppError> ConflictAsync(AuditableEntity entity, CancellationToken cancellationToken, string? currentVersion = null)
    {
        var editorId = entity.UpdatedBy ?? entity.CreatedBy;
        var editor = editorId is { } id ? await accounts.GetByIdAsync(id, cancellationToken) : null;
        var time = BusinessClock.Format(entity.UpdatedAt ?? entity.CreatedAt);
        return AppError.Conflict(
            UserMessages.ConcurrentUpdate(editor?.DisplayName ?? "another user", time),
            new Dictionary<string, object?> { ["currentRowVersion"] = currentVersion ?? Encode(entity.RowVersion) });
    }

    public static string Encode(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>True when <paramref name="rowVersion"/> can be read as a row version (base64), whatever its value.</summary>
    public static bool IsWellFormed(string rowVersion) => TryDecode(rowVersion, out _);

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
