using System.Globalization;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>The default code patterns of input items: GI000001 for seed, VT000001 for materials, HK000001 for the others, one sequence each.</summary>
public static class FarmingInputItemCodes
{
    public static string SequenceKeyOf(InputItemGroup group) => group switch
    {
        InputItemGroup.Seed => DocumentSequenceKeys.InputItemSeed,
        InputItemGroup.Material => DocumentSequenceKeys.InputItemMaterial,
        _ => DocumentSequenceKeys.InputItemOther,
    };

    /// <summary>The prefix followed by at least six digits: GI000001…GI999999, then GI1000000.</summary>
    public static string Format(InputItemGroup group, long number) => $"{InputItemUnitRules.PrefixOf(group)}{number.ToString("000000", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// The codes of the input items: generated from the sequence of the item type, or typed by the user (1–30 ASCII letters, digits, "-", "_" or "."). A code is unique among the item
/// codes and the unit codes, regardless of letter case.
/// </summary>
public sealed class FarmingInputItemCodeService(IFarmingInputItemRepository items, IDocumentNumberAllocator numbers)
{
    /// <summary>The code an item of the type would get now, without spending its number: the sequence is read as the next number after what is already used.</summary>
    public async Task<string> PeekAsync(InputItemGroup group, CancellationToken cancellationToken)
    {
        var number = await numbers.PeekAsync(FarmingInputItemCodes.SequenceKeyOf(group), cancellationToken);
        while (true)
        {
            var code = FarmingInputItemCodes.Format(group, number);
            if (await items.FindCodeOwnerAsync(code, null, null, cancellationToken) is null)
            {
                return code;
            }

            number++;
        }
    }

    /// <summary>The next generated code; a number whose code someone already typed is spent and skipped, never reused.</summary>
    public async Task<string> NextAsync(InputItemGroup group, CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = FarmingInputItemCodes.Format(group, await numbers.NextAsync(FarmingInputItemCodes.SequenceKeyOf(group), cancellationToken));
            if (await items.FindCodeOwnerAsync(code, null, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>The code of a new item: the typed one when free, otherwise (left empty) the next generated one. Call it once the form is valid, so a refused save spends no number.</summary>
    public async Task<(string? Code, AppError? Error)> ForNewAsync(string? typed, InputItemGroup group, CancellationToken cancellationToken)
    {
        if (!Purchasing.DocumentCodes.TypedAllowed(typed))
        {
            return (null, AppError.InvalidFields(["Code"]));
        }

        if (Purchasing.DocumentCodes.Typed(typed) is not { } code)
        {
            return (await NextAsync(group, cancellationToken), null);
        }

        return (code, await DuplicateAsync(code, null, null, cancellationToken));
    }

    /// <summary>The code after an edit: the stored one when the request leaves it empty or sends it again, otherwise the typed one when free.</summary>
    public async Task<(string? Code, AppError? Error)> ForEditAsync(string? typed, Guid itemId, string storedCode, CancellationToken cancellationToken)
    {
        if (!Purchasing.DocumentCodes.TypedAllowed(typed))
        {
            return (null, AppError.InvalidFields(["Code"]));
        }

        if (Purchasing.DocumentCodes.Typed(typed) is not { } code || string.Equals(code, storedCode, StringComparison.Ordinal))
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(code, itemId, null, cancellationToken));
    }

    /// <summary>MSG-GEN-02 with the item that owns the code; <c>field</c> is "Code" for an item code and "Unit code" for the code of a unit.</summary>
    public async Task<AppError?> DuplicateAsync(string code, Guid? exceptItemId, Guid? exceptItemUnitId, CancellationToken cancellationToken) =>
        await items.FindCodeOwnerAsync(code, exceptItemId, exceptItemUnitId, cancellationToken) is { } owner
            ? AppError.AlreadyExists("Farming input item", owner.ItemId, owner.IsUnitCode ? "Unit code" : "Code")
            : null;
}
