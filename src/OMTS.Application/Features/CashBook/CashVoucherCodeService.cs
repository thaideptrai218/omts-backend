using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.CashBook;

/// <summary>
/// The code of a cash voucher: typed by the user, or given from the sequence of its fund and direction when left empty (TTM/CTM
/// Cash, TNH/CNH Bank, TVD/CVD EWallet). Codes are unique among cash vouchers regardless of letter case, cancelled ones included.
/// </summary>
public sealed class CashVoucherCodeService(IDocumentNumberAllocator numbers, ICashVoucherRepository vouchers)
{
    /// <summary>
    /// The next free code of the sequence: a number whose code a user already typed (TTM000050 entered by hand) is skipped and
    /// spent, so a generated code never clashes and a number is never given twice.
    /// </summary>
    public async Task<string> NextAsync(CashFund fund, CashDirection direction, CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = CashBookRules.GeneratedCode(fund, direction, await numbers.NextAsync(CashBookRules.SequenceKey(fund, direction), cancellationToken));
            if (await vouchers.FindIdByCodeAsync(code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>
    /// The code a new voucher gets: the typed one when no other voucher has it, the next generated one when left empty. Call it once
    /// the voucher is valid, so a refused save spends no number.
    /// </summary>
    public async Task<(string Code, AppError? Error)> ForNewAsync(CashFund fund, CashDirection direction, string? typed, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code)
        {
            return (await NextAsync(fund, direction, cancellationToken), null);
        }

        return (code, await DuplicateAsync(code, null, cancellationToken));
    }

    /// <summary>The code a voucher keeps on an edit: the stored one when the request leaves it empty or sends it again, otherwise the typed one when no other voucher has it.</summary>
    public async Task<(string Code, AppError? Error)> ForEditAsync(string? typed, Guid voucherId, string storedCode, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code || code == storedCode)
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(code, voucherId, cancellationToken));
    }

    /// <summary>MSG-GEN-02 (409, <c>field</c> "Code", <c>existingId</c>) when another voucher already has the code.</summary>
    private async Task<AppError?> DuplicateAsync(string code, Guid? exceptVoucherId, CancellationToken cancellationToken) =>
        await vouchers.FindIdByCodeAsync(code, exceptVoucherId, cancellationToken) is { } existingId
            ? AppError.AlreadyExists("Cash voucher code", existingId, "Code")
            : null;
}
