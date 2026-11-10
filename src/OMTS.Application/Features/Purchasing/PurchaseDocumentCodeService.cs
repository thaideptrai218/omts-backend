using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Purchasing;

/// <summary>
/// The code of a purchase receipt (PN), a purchase return (TH) or a supplier payment (TT): typed by the user, or given from the
/// document sequence when left empty. Codes are unique per kind regardless of letter case, Draft and Cancelled documents included.
/// </summary>
public sealed class PurchaseDocumentCodeService(IDocumentNumberAllocator numbers, IPurchaseDocumentCodeQuery codes)
{
    /// <summary>MSG-GEN-02 (409, <c>field</c> "Code", <c>existingId</c>) when another document of the kind already has the code.</summary>
    public async Task<AppError?> DuplicateAsync(PurchaseDocumentKind kind, string code, Guid? exceptId, CancellationToken cancellationToken) =>
        await codes.FindAsync(kind, code, exceptId, cancellationToken) is { } existingId
            ? AppError.AlreadyExists(ObjectName(kind), existingId, "Code")
            : null;

    /// <summary>
    /// The next free code of the sequence: a number whose code a user already typed (e.g. PN000050 entered by hand) is skipped
    /// and spent, so a generated code never clashes and a number is never given twice.
    /// </summary>
    public async Task<string> NextAsync(PurchaseDocumentKind kind, CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = kind switch
            {
                PurchaseDocumentKind.Receipt => DocumentCodes.ReceiptCode(await numbers.NextAsync(DocumentSequenceKeys.PurchaseReceipt, cancellationToken)),
                PurchaseDocumentKind.Return => DocumentCodes.ReturnCode(await numbers.NextAsync(DocumentSequenceKeys.PurchaseReturn, cancellationToken)),
                PurchaseDocumentKind.Payment => DocumentCodes.PaymentCode(await numbers.NextAsync(DocumentSequenceKeys.SupplierPayment, cancellationToken)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
            if (await codes.FindAsync(kind, code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>
    /// The code a new document gets: the typed one (<see cref="DocumentCodes.Typed"/>) when no other document has it, the next
    /// generated one when left empty. Call it once the document is valid, so a refused save spends no number.
    /// </summary>
    public async Task<(string Code, AppError? Error)> ForNewAsync(PurchaseDocumentKind kind, string? typed, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code)
        {
            return (await NextAsync(kind, cancellationToken), null);
        }

        return (code, await DuplicateAsync(kind, code, null, cancellationToken));
    }

    /// <summary>
    /// The code a Draft keeps or gets on a save: the stored one when the request leaves it empty or sends it again, otherwise the
    /// typed one when no other document has it.
    /// </summary>
    public async Task<(string Code, AppError? Error)> ForDraftAsync(
        PurchaseDocumentKind kind, string? typed, Guid documentId, string storedCode, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code || code == storedCode)
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(kind, code, documentId, cancellationToken));
    }

    private static string ObjectName(PurchaseDocumentKind kind) => kind switch
    {
        PurchaseDocumentKind.Receipt => "Purchase receipt code",
        PurchaseDocumentKind.Return => "Purchase return code",
        _ => "Supplier payment code",
    };
}
