using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>The codes of the customer profiles: KH000123 from one sequence, or a code the user types (a label, unique regardless of letter case).</summary>
public sealed class CustomerCodeService(ICustomerRepository customers, IDocumentNumberAllocator numbers)
{
    /// <summary>The next generated code; a number whose code someone already typed is spent and skipped, never reused.</summary>
    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = SalesDocumentCodes.CustomerCode(await numbers.NextAsync(DocumentSequenceKeys.Customer, cancellationToken));
            if (await customers.FindByCodeAsync(code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>
    /// The code a new profile gets: the typed one when no other profile has it, otherwise the next generated one. Call it once the form is valid, so a refused save spends no
    /// number. MSG-GEN-01 (field Code) for a code with characters that are not allowed, MSG-GEN-02 for one that is taken.
    /// </summary>
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

        return (code, await DuplicateAsync(code, null, cancellationToken));
    }

    /// <summary>The code after an edit: the stored one when the request leaves it empty or sends it again, otherwise the typed one when no other profile has it.</summary>
    public async Task<(string? Code, AppError? Error)> ForEditAsync(string? typed, Guid customerId, string storedCode, CancellationToken cancellationToken)
    {
        if (!DocumentCodes.TypedAllowed(typed))
        {
            return (null, AppError.InvalidFields(["Code"]));
        }

        if (DocumentCodes.Typed(typed) is not { } code || string.Equals(code, storedCode, StringComparison.Ordinal))
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(code, customerId, cancellationToken));
    }

    private async Task<AppError?> DuplicateAsync(string code, Guid? exceptId, CancellationToken cancellationToken) =>
        await customers.FindByCodeAsync(code, exceptId, cancellationToken) is { } other ? AppError.AlreadyExists("Customer code", other.Id, "Code") : null;
}
