using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.ReceivingBankAccounts;

/// <summary>
/// DEBT-01 – the Cooperative Owner sets the receiving bank account. In the order of the SRS: check (step 3), the service
/// accepts (step 4), MSG-DEBT-01 (step 5), save (step 7). Each request asks the service, also the confirming one: a client
/// cannot save an account the service did not accept, and the time saved is that of the latest answer.
/// </summary>
public sealed class ReceivingBankAccountService(
    ICooperativeRepository cooperatives,
    ReceivingAccountAcceptance acceptance,
    ISentInvoiceQrCodeQuery sentCodes,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<ReceivingBankAccountView>> GetAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative ? View(cooperative) : AppError.NotFound;

    public async Task<Result<ReceivingBankAccountSaved>> SaveAsync(SaveReceivingBankAccountRequest request, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (cooperative is null)
        {
            return AppError.NotFound;
        }

        var (account, invalid) = ReceivingBankAccountRules.Read(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await rowVersions.CheckAsync(cooperative, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        // No transaction is open during this wait: the read above is over and the save below is one statement batch. A
        // save of the cooperative in the meantime makes that save fail on the row version read above (409 MSG06).
        var answer = await acceptance.AskAsync(account, cancellationToken);
        if (answer.Outcome != AccountAcceptanceOutcome.Accepted)
        {
            return NotAccepted(account, answer.Outcome);
        }

        var acceptedAt = timeProvider.GetUtcNow();
        if (!request.ConfirmAccount)
        {
            return await ConfirmationAsync(cooperative.BankAccount, account, cancellationToken);
        }

        var before = Snapshot(cooperative.BankAccount);
        cooperative.BankAccount = new ReceivingBankAccount
        {
            BankBin = account.BankBin,
            BankName = account.BankName,
            AccountNumber = account.AccountNumber,
            AccountHolderName = account.AccountHolderName,
            AcceptedAt = acceptedAt,
        };
        // The account is an owned row: without this the row version of the cooperative is neither checked nor moved.
        unitOfWork.MarkChanged(cooperative);
        audit.Record(new AuditEntry("Cooperative.ReceivingBankAccountChanged", nameof(Cooperative))
        {
            RecordId = cooperative.Id,
            Before = before,
            After = Snapshot(cooperative.BankAccount),
        });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Overtaken during the wait, the normal way to lose this race. The same answer as the check above, from the
            // row as it now is: the version to read again, who saved and when. The request ends here and saves nothing more.
            await unitOfWork.ReloadAsync(cooperative, cancellationToken);
            return await rowVersions.ConflictAsync(cooperative, cancellationToken);
        }

        var saved = UserMessages.Saved("Receiving bank account");
        return new ReceivingBankAccountSaved(View(cooperative), saved.Text, saved.Code);
    }

    /// <summary>409 for a refusal, 503 for no answer: the same message, but only the second is worth a plain retry.</summary>
    private static AppError NotAccepted(ReceivingAccountToAccept account, AccountAcceptanceOutcome outcome) => new(
        outcome == AccountAcceptanceOutcome.Rejected ? ErrorKind.Conflict : ErrorKind.ServiceUnavailable,
        UserMessages.ReceivingBankAccountNotAccepted(account.AccountNumber, account.BankName));

    private async Task<AppError> ConfirmationAsync(ReceivingBankAccount? current, ReceivingAccountToAccept account, CancellationToken cancellationToken)
    {
        // Codes already sent stay valid when the bank and the number stay (a corrected holder name, the same account saved again).
        int? oldCodes = current is not null && (current.BankBin != account.BankBin || current.AccountNumber != account.AccountNumber)
            ? await sentCodes.CountUnpaidInvoicesWithSentCodesAsync(cancellationToken)
            : null;
        var oldCodesText = oldCodes is { } count ? UserMessages.OldVietQrCodesText(count) : string.Empty;

        return AppError.Conflict(
            UserMessages.UseReceivingBankAccount(oldCodesText, account.AccountNumber, account.BankName, account.AccountHolderName),
            new Dictionary<string, object?>
            {
                ["confirmFlag"] = "confirmAccount",
                ["accountNumber"] = account.AccountNumber,
                ["bankName"] = account.BankName,
                ["accountHolder"] = account.AccountHolderName,
                ["oldCodesCount"] = oldCodes,
            });
    }

    /// <summary>The number is masked in the audit log, as for the bank account of an employee.</summary>
    private static object? Snapshot(ReceivingBankAccount? account) => account is null
        ? null
        : new
        {
            account.BankBin,
            account.BankName,
            AccountNumber = Masking.ReceivingAccountNumber(account.AccountNumber),
            account.AccountHolderName,
            account.AcceptedAt,
        };

    private static ReceivingBankAccountView View(Cooperative cooperative) => new(
        cooperative.BankAccount is { } account
            ? new ReceivingBankAccountDetails(account.BankBin, account.BankName, account.AccountNumber, account.AccountHolderName, account.AcceptedAt)
            : null,
        RowVersionGuard.Encode(cooperative.RowVersion));
}
