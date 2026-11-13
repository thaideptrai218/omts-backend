using System.Globalization;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Issues;

/// <summary>The codes of the input issues: XD000001 for a use, XH000001 for a disposal (one sequence each), or a code the user types, unique regardless of letter case across every issue.</summary>
public sealed class InputIssueCodeService(IInputIssueRepository issues, IDocumentNumberAllocator numbers)
{
    public static string Format(InputIssueKind kind, long number) =>
        $"{PrefixOf(kind)}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    public static string PrefixOf(InputIssueKind kind) => kind == InputIssueKind.Use ? DocumentSequenceKeys.InputIssueUse : DocumentSequenceKeys.InputIssueDisposal;

    /// <summary>The code the next issue of the kind would get, without spending its number.</summary>
    public async Task<string> PeekAsync(InputIssueKind kind, CancellationToken cancellationToken)
    {
        var number = await numbers.PeekAsync(PrefixOf(kind), cancellationToken);
        while (await issues.FindByCodeAsync(Format(kind, number), null, cancellationToken) is not null)
        {
            number++;
        }

        return Format(kind, number);
    }

    /// <summary>The next generated code; a number whose code someone already typed is spent and skipped.</summary>
    public async Task<string> NextAsync(InputIssueKind kind, CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = Format(kind, await numbers.NextAsync(PrefixOf(kind), cancellationToken));
            if (await issues.FindByCodeAsync(code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>The code of a new issue: the typed one when free, otherwise (left empty) the next generated one. Call it once the form is valid, so a refused save spends no number.</summary>
    public async Task<(string? Code, AppError? Error)> ForNewAsync(string? typed, InputIssueKind kind, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code)
        {
            return (await NextAsync(kind, cancellationToken), null);
        }

        return (code, await DuplicateAsync(code, null, cancellationToken));
    }

    /// <summary>The code after an edit: the stored one when the request leaves it empty or sends it again, otherwise the typed one when free.</summary>
    public async Task<(string? Code, AppError? Error)> ForEditAsync(string? typed, Guid issueId, string storedCode, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code || string.Equals(code, storedCode, StringComparison.Ordinal))
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(code, issueId, cancellationToken));
    }

    public async Task<AppError?> DuplicateAsync(string code, Guid? exceptIssueId, CancellationToken cancellationToken) =>
        await issues.FindByCodeAsync(code, exceptIssueId, cancellationToken) is { } other ? AppError.AlreadyExists("Input issue", other.Id, "Code") : null;
}
