using System.Globalization;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>The rules of the codes and units of a product: SP000001 by default, kg as the base unit, extra units with a factor in kg. The helpers of the input item units (letter of a unit, factor range, free code) are used as they are.</summary>
public static class ProductRules
{
    public const string Prefix = "SP";

    /// <summary>The name of the base unit in the cooperative's unit list: prices, invoices and stock are all per kg.</summary>
    public const string BaseUnitName = "kg";

    public const int MaxUnits = 20;
    public const int MaxPhotos = 5;

    /// <summary>The prefix followed by at least six digits: SP000001…SP999999, then SP1000000.</summary>
    public static string Format(long number) => $"{Prefix}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>The price a new extra unit starts at: its factor times the price per kg, whole VND half up, at least 1.</summary>
    public static decimal DefaultUnitPrice(decimal factor, decimal basePrice) => Math.Max(1m, decimal.Round(factor * basePrice, 0, MidpointRounding.AwayFromZero));

    public static bool IsBaseUnitName(string? name) => string.Equals(name?.Trim(), BaseUnitName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The upper-case first letter of a unit name without diacritics, as an item unit code takes it.</summary>
    public static string DefaultUnitCode(string productCode, string unitName) =>
        $"{productCode}-{Purchasing.Items.InputItemUnitRules.LetterOf(unitName)}";
}

/// <summary>
/// The codes of the products: generated from the SP sequence, or typed by the user (1–30 ASCII letters, digits, "-", "_" or "."). A code is unique among the product codes and the product unit codes,
/// regardless of letter case; a number whose code someone typed is spent and skipped, never reused.
/// </summary>
public sealed class ProductCodeService(IProductRepository products, IDocumentNumberAllocator numbers)
{
    /// <summary>The code a product would get now, without spending its number.</summary>
    public async Task<string> PeekAsync(CancellationToken cancellationToken)
    {
        var number = await numbers.PeekAsync(DocumentSequenceKeys.Product, cancellationToken);
        while (await products.FindCodeOwnerAsync(ProductRules.Format(number), null, null, cancellationToken) is not null)
        {
            number++;
        }

        return ProductRules.Format(number);
    }

    /// <summary>The next generated code.</summary>
    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = ProductRules.Format(await numbers.NextAsync(DocumentSequenceKeys.Product, cancellationToken));
            if (await products.FindCodeOwnerAsync(code, null, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>The next generated code that is also not in <paramref name="reserved"/>: the codes typed in an import file, which are saved in the same save as the generated ones.</summary>
    public async Task<string> NextAvoidingAsync(IReadOnlySet<string> reserved, CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = await NextAsync(cancellationToken);
            if (!reserved.Contains(code))
            {
                return code;
            }
        }
    }

    /// <summary>The code of a new product: the typed one when free, otherwise (left empty) the next generated one. Call it once the form is valid, so a refused save spends no number.</summary>
    public async Task<(string? Code, AppError? Error)> ForNewAsync(string? typed, CancellationToken cancellationToken)
    {
        if (!DocumentCodes.TypedAllowed(typed))
        {
            return (null, AppError.InvalidFields(["Code"]));
        }

        if (DocumentCodes.Typed(typed) is not { } code)
        {
            return (await NextAsync(cancellationToken), null);
        }

        return (code, await DuplicateAsync(code, null, null, cancellationToken));
    }

    /// <summary>The code after an edit: the stored one when the request leaves it empty or sends it again, otherwise the typed one when free.</summary>
    public async Task<(string? Code, AppError? Error)> ForEditAsync(string? typed, Guid productId, string storedCode, CancellationToken cancellationToken)
    {
        if (!DocumentCodes.TypedAllowed(typed))
        {
            return (null, AppError.InvalidFields(["Code"]));
        }

        if (DocumentCodes.Typed(typed) is not { } code || string.Equals(code, storedCode, StringComparison.Ordinal))
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(code, productId, null, cancellationToken));
    }

    /// <summary>MSG-GEN-02 with the product that owns the code; the field is "Code" for a product code and "Unit code" for the code of a unit.</summary>
    public async Task<AppError?> DuplicateAsync(string code, Guid? exceptProductId, Guid? exceptProductUnitId, CancellationToken cancellationToken) =>
        await products.FindCodeOwnerAsync(code, exceptProductId, exceptProductUnitId, cancellationToken) is { } owner
            ? AppError.AlreadyExists("Product", owner.ItemId, owner.IsUnitCode ? "Unit code" : "Code")
            : null;
}
