using System.Globalization;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>The codes of the harvest plans: KHT000001, or a code the user types, unique regardless of letter case across every plan, drafts and cancelled ones included.</summary>
public sealed class HarvestPlanCodeService(IHarvestPlanRepository plans, IDocumentNumberAllocator numbers)
{
    public const string ObjectName = "Harvest plan";

    public static string Format(long number) => $"{DocumentSequenceKeys.HarvestPlan}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>The code the next plan would get, without spending its number.</summary>
    public async Task<string> PeekAsync(CancellationToken cancellationToken)
    {
        var number = await numbers.PeekAsync(DocumentSequenceKeys.HarvestPlan, cancellationToken);
        while (await plans.FindByCodeAsync(Format(number), null, cancellationToken) is not null)
        {
            number++;
        }

        return Format(number);
    }

    /// <summary>The next generated code; a number whose code someone already typed is spent and skipped.</summary>
    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = Format(await numbers.NextAsync(DocumentSequenceKeys.HarvestPlan, cancellationToken));
            if (await plans.FindByCodeAsync(code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>The code of a new plan: the typed one when free, otherwise (left empty) the next generated one. Call it once the form is valid, so a refused save spends no number.</summary>
    public async Task<(string? Code, AppError? Error)> ForNewAsync(string? typed, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code)
        {
            return (await NextAsync(cancellationToken), null);
        }

        return (code, await DuplicateAsync(code, null, cancellationToken));
    }

    /// <summary>The code after an edit: the stored one when the request leaves it empty or sends it again, otherwise the typed one when free.</summary>
    public async Task<(string Code, AppError? Error)> ForEditAsync(string? typed, Guid planId, string storedCode, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code || string.Equals(code, storedCode, StringComparison.Ordinal))
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(code, planId, cancellationToken));
    }

    /// <summary>MSG-GEN-02 (409, <c>field</c> "Code", <c>existingId</c>) when another plan has the code.</summary>
    public async Task<AppError?> DuplicateAsync(string code, Guid? exceptPlanId, CancellationToken cancellationToken) =>
        await plans.FindByCodeAsync(code, exceptPlanId, cancellationToken) is { } other ? AppError.AlreadyExists(ObjectName, other.Id, "Code") : null;
}
