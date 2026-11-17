using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The codes of the invoices: HD000001, or a code the user types, unique regardless of letter case across every invoice (cancelled ones included) and every sale awaiting its transfer, which holds
/// the code of its invoice meanwhile.
/// </summary>
public sealed class SalesInvoiceCodeService(ISalesInvoiceRepository invoices, IPendingSaleRepository pendingSales, IDocumentNumberAllocator numbers)
{
    public const string ObjectName = "Sales invoice";

    /// <summary>The code the next invoice would get, without spending its number.</summary>
    public async Task<string> PeekAsync(CancellationToken cancellationToken)
    {
        var number = await numbers.PeekAsync(DocumentSequenceKeys.SalesInvoice, cancellationToken);
        while (await TakenAsync(SalesDocumentCodes.InvoiceCode(number), null, cancellationToken))
        {
            number++;
        }

        return SalesDocumentCodes.InvoiceCode(number);
    }

    /// <summary>The next generated code; a number whose code someone already typed is spent and skipped.</summary>
    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = SalesDocumentCodes.InvoiceCode(await numbers.NextAsync(DocumentSequenceKeys.SalesInvoice, cancellationToken));
            if (!await TakenAsync(code, null, cancellationToken))
            {
                return code;
            }
        }
    }

    /// <summary>MSG-GEN-02 (409, <c>field</c> "Code", <c>existingId</c>) when another invoice, or a sale awaiting its transfer, has the code; null when it is free.</summary>
    public async Task<AppError?> DuplicateAsync(string code, Guid? exceptInvoiceId, CancellationToken cancellationToken)
    {
        if (await invoices.FindOtherByCodeAsync(code, exceptInvoiceId, cancellationToken) is { } id)
        {
            return AppError.AlreadyExists(ObjectName, id, "Code");
        }

        return await pendingSales.GetAsync(code, cancellationToken) is { } sale ? AppError.AlreadyExists("Sale awaiting its transfer", sale.Id, "Code") : null;
    }

    /// <summary>The code after an edit: the stored one when the request leaves it empty or sends it again, otherwise the typed one when free.</summary>
    public async Task<(string Code, AppError? Error)> ForEditAsync(string? typed, Guid invoiceId, string storedCode, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code || string.Equals(code, storedCode, StringComparison.Ordinal))
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(code, invoiceId, cancellationToken));
    }

    private async Task<bool> TakenAsync(string code, Guid? exceptInvoiceId, CancellationToken cancellationToken) =>
        await DuplicateAsync(code, exceptInvoiceId, cancellationToken) is not null;
}
