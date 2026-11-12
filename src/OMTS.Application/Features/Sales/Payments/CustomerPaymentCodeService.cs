using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>The codes of the customer payments: PT000123 from one sequence, or a code the user types (unique regardless of letter case; it is the transfer content of a VietQR code).</summary>
public sealed class CustomerPaymentCodeService(ICustomerPaymentRepository payments, IDocumentNumberAllocator numbers)
{
    /// <summary>The next generated code; a number whose code someone already typed is spent and skipped, never reused.</summary>
    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = SalesDocumentCodes.PaymentCode(await numbers.NextAsync(DocumentSequenceKeys.CustomerPayment, cancellationToken));
            if (await payments.FindIdByCodeAsync(code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>
    /// The code a new payment gets: the typed one when no other payment has it (MSG-GEN-02, field Code, otherwise), the next generated one when left empty. Call it once the
    /// payment is valid, so a refused save spends no number.
    /// </summary>
    public async Task<(string Code, AppError? Error)> ForNewAsync(string? typed, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code)
        {
            return (await NextAsync(cancellationToken), null);
        }

        return (code, await payments.FindIdByCodeAsync(code, null, cancellationToken) is { } existingId
            ? AppError.AlreadyExists("Customer payment code", existingId, "Code")
            : null);
    }
}
