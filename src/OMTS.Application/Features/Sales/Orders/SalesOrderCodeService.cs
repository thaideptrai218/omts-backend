using System.Globalization;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>The codes of the orders: DH000001, or a code the user types, unique regardless of letter case across every order, drafts and cancelled ones included.</summary>
public sealed class SalesOrderCodeService(ISalesOrderRepository orders, IDocumentNumberAllocator numbers)
{
    public const string ObjectName = "Sales order";

    public static string Format(long number) => $"{DocumentSequenceKeys.SalesOrder}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>The code the next order would get, without spending its number.</summary>
    public async Task<string> PeekAsync(CancellationToken cancellationToken)
    {
        var number = await numbers.PeekAsync(DocumentSequenceKeys.SalesOrder, cancellationToken);
        while (await orders.FindByCodeAsync(Format(number), null, cancellationToken) is not null)
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
            var code = Format(await numbers.NextAsync(DocumentSequenceKeys.SalesOrder, cancellationToken));
            if (await orders.FindByCodeAsync(code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>The code of a new order: the typed one when free, otherwise (left empty) the next generated one. Call it once the form is valid, so a refused save spends no number.</summary>
    public async Task<(string? Code, AppError? Error)> ForNewAsync(string? typed, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code)
        {
            return (await NextAsync(cancellationToken), null);
        }

        return (code, await DuplicateAsync(code, null, cancellationToken));
    }

    /// <summary>The code after an edit: the stored one when the request leaves it empty or sends it again, otherwise the typed one when free.</summary>
    public async Task<(string Code, AppError? Error)> ForEditAsync(string? typed, Guid orderId, string storedCode, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code || string.Equals(code, storedCode, StringComparison.Ordinal))
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(code, orderId, cancellationToken));
    }

    /// <summary>MSG-GEN-02 (409, <c>field</c> "Code", <c>existingId</c>) when another order has the code.</summary>
    public async Task<AppError?> DuplicateAsync(string code, Guid? exceptOrderId, CancellationToken cancellationToken) =>
        await orders.FindByCodeAsync(code, exceptOrderId, cancellationToken) is { } other ? AppError.AlreadyExists(ObjectName, other.Id, "Code") : null;
}
