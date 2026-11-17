using OMTS.Application.Common.Results;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// A purchase receipt is either a Seed receipt (only Seed items: every line is a seed lot) or a Supply receipt (Material and Other items, no seed). The kind is set when the receipt is created, from the kind sent or the
/// first item of its lines, and never changes; a purchase return takes the kind of its receipt.
/// </summary>
public static class ReceiptKindRules
{
    public const string LinesField = "Lines";
    public const string KindField = "Kind";
    public const string SeedOnlyHint = "A seed receipt holds only seed items";
    public const string NoSeedHint = "A supply receipt holds no seed item";

    public static PurchaseReceiptKind KindOf(InputItemGroup group) => group == InputItemGroup.Seed ? PurchaseReceiptKind.Seed : PurchaseReceiptKind.Supply;

    /// <summary>The kind a new receipt takes: the one sent, else that of the first line that has an item, else Supply (a Draft with no item yet).</summary>
    public static PurchaseReceiptKind ForNew(PurchaseReceiptKind? requested, IEnumerable<InputItemGroup?> lineGroups)
    {
        if (requested is { } kind)
        {
            return kind;
        }

        var first = lineGroups.FirstOrDefault(g => g is not null);
        return first is { } group ? KindOf(group) : PurchaseReceiptKind.Supply;
    }

    /// <summary>True when seed items and others are on one document: a return without a receipt then has no kind.</summary>
    public static bool MixesKinds(IEnumerable<InputItemGroup?> lineGroups)
    {
        var kinds = lineGroups.Where(g => g is not null).Select(g => KindOf(g!.Value)).Distinct().Count();
        return kinds > 1;
    }

    public static AppError MixedReturn() => AppError.Invalid(LinesField, "A return holds only seed items or only supply items");

    public static bool IsDefined(PurchaseReceiptKind? kind) => kind is null || Enum.IsDefined(kind.Value);

    /// <summary>MSG-GEN-01 on <c>Lines</c> when one of the items does not belong to the kind; null when they all do. A line without an item (a Draft) is not judged.</summary>
    public static AppError? Mismatch(PurchaseReceiptKind kind, IEnumerable<InputItemGroup?> lineGroups)
    {
        var wrong = lineGroups.Any(g => g is { } group && KindOf(group) != kind);
        return wrong ? AppError.Invalid(LinesField, kind == PurchaseReceiptKind.Seed ? SeedOnlyHint : NoSeedHint) : null;
    }
}
