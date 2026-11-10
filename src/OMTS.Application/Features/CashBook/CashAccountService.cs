using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.CashBook;

/// <summary>
/// The bank accounts and e-wallets of the cash book. The account that matches the cooperative's receiving bank account (the one
/// the VietQR codes pay into) is marked <c>isVietQr</c> and cannot be deleted, put out of use or moved to another bank or number
/// (MSG-CASH-01); saving the receiving account keeps a matching cash account (<see cref="SyncVietQrAccountAsync"/>). An account
/// that a voucher or a supplier payment names is put out of use instead of deleted.
/// </summary>
public sealed class CashAccountService(
    ICashAccountRepository accounts,
    ICooperativeRepository cooperatives,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(CashAccount);

    /// <summary>The list of banks for the account form; open to whoever may manage or read the cash book, as the Owner-only bank list is not.</summary>
    public static IReadOnlyList<BankView> Banks() => BankCatalog.All;

    public async Task<IReadOnlyList<CashAccountView>> ListAsync(CashAccountKind? kind, bool includeInactive, CancellationToken cancellationToken)
    {
        var vietQr = (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount;
        return (await accounts.ListAsync(kind, includeInactive, cancellationToken)).Select(a => View(a, vietQr)).ToList();
    }

    public async Task<Result<CashAccountView>> CreateAsync(SaveCashAccountRequest request, CancellationToken cancellationToken)
    {
        var bank = request.Kind == CashAccountKind.Bank ? BankCatalog.FindByBin(request.BankBin?.Trim()) : null;
        var invalid = CashBookRules.InvalidAccount(request, bank?.ShortName);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var account = new CashAccount { IsActive = true };
        Apply(account, request, bank);
        if (await DuplicateAsync(account, null, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        accounts.Add(account);
        audit.Record(new AuditEntry("CashAccount.Created", RecordType) { RecordId = account.Id, After = Snapshot(account) });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(account, cancellationToken);
    }

    public async Task<Result<CashAccountView>> UpdateAsync(Guid accountId, SaveCashAccountRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.GetForEditAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        if (await rowVersions.CheckAsync(account, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var bank = request.Kind == CashAccountKind.Bank ? BankCatalog.FindByBin(request.BankBin?.Trim()) : null;
        var invalid = CashBookRules.InvalidAccount(request, bank?.ShortName);
        if (invalid.Count == 0 && request.Kind != account.Kind && await accounts.IsUsedAsync(account.Id, cancellationToken))
        {
            // A voucher of the fund of the old kind would name an account of another kind.
            invalid.Add("Kind");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var cooperativeAccount = (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount;
        var before = Snapshot(account);
        var wasVietQr = IsVietQr(account, cooperativeAccount);
        Apply(account, request, bank);
        account.IsActive = request.IsActive;
        if (wasVietQr && (!IsVietQr(account, cooperativeAccount) || !account.IsActive))
        {
            // The edit would stop it being the VietQR account: nothing is saved.
            await unitOfWork.ReloadAsync(account, cancellationToken);
            return AppError.Conflict(UserMessages.CashAccountIsVietQr);
        }

        if (await DuplicateAsync(account, account.Id, cancellationToken) is { } duplicate)
        {
            await unitOfWork.ReloadAsync(account, cancellationToken);
            return duplicate;
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(account));
        if (changedAfter is not null)
        {
            unitOfWork.MarkChanged(account);
            audit.Record(new AuditEntry("CashAccount.Edited", RecordType)
            {
                RecordId = account.Id, Before = changedBefore, After = changedAfter,
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await ViewAsync(account, cancellationToken);
    }

    /// <summary>
    /// Deletes an account nothing uses (the result is null: 204), otherwise puts it out of use and returns it. The VietQR account
    /// is neither: MSG-CASH-01.
    /// </summary>
    public async Task<Result<CashAccountView?>> DeleteAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await accounts.GetForEditAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        var cooperativeAccount = (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount;
        if (IsVietQr(account, cooperativeAccount))
        {
            return AppError.Conflict(UserMessages.CashAccountIsVietQr);
        }

        if (!await accounts.IsUsedAsync(account.Id, cancellationToken))
        {
            accounts.Remove(account);
            audit.Record(new AuditEntry("CashAccount.Deleted", RecordType) { RecordId = account.Id, Before = Snapshot(account) });
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<CashAccountView?>.Success(null);
        }

        if (account.IsActive)
        {
            account.IsActive = false;
            unitOfWork.MarkChanged(account);
            audit.Record(new AuditEntry("CashAccount.Deactivated", RecordType)
            {
                RecordId = account.Id, Before = new { IsActive = true }, After = new { IsActive = false },
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<CashAccountView?>.Success(View(account, cooperativeAccount));
    }

    /// <summary>
    /// Keeps a cash account in step with the receiving bank account being saved: the Bank account of the same bank and number is
    /// brought back into use with the new holder name, or one is made. Added to the unit of work of the caller, which saves it
    /// with the receiving account; the account the cooperative used before stays as an ordinary account.
    /// </summary>
    public async Task SyncVietQrAccountAsync(ReceivingBankAccount receiving, CancellationToken cancellationToken)
    {
        var existing = await accounts.FindBankAccountAsync(receiving.BankBin, receiving.AccountNumber, cancellationToken);
        if (existing is null)
        {
            var created = new CashAccount
            {
                Kind = CashAccountKind.Bank,
                BankBin = receiving.BankBin,
                ProviderName = receiving.BankName,
                AccountNumber = receiving.AccountNumber,
                HolderName = receiving.AccountHolderName,
                IsActive = true,
            };
            accounts.Add(created);
            audit.Record(new AuditEntry("CashAccount.Created", RecordType) { RecordId = created.Id, After = Snapshot(created) });
            return;
        }

        var before = Snapshot(existing);
        existing.ProviderName = receiving.BankName;
        existing.HolderName = receiving.AccountHolderName;
        existing.IsActive = true;
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(existing));
        if (changedAfter is not null)
        {
            unitOfWork.MarkChanged(existing);
            audit.Record(new AuditEntry("CashAccount.Edited", RecordType) { RecordId = existing.Id, Before = changedBefore, After = changedAfter });
        }
    }

    /// <summary>A Bank account takes its provider from the bank list and keeps its bin; an EWallet keeps its provider as typed and has no bin.</summary>
    private static void Apply(CashAccount account, SaveCashAccountRequest request, BankView? bank)
    {
        account.Kind = request.Kind!.Value;
        account.BankBin = bank?.Bin;
        account.ProviderName = bank?.ShortName ?? request.ProviderName!.Trim();
        account.AccountNumber = request.AccountNumber!.Trim();
        account.HolderName = request.HolderName!.Trim();
        account.Note = SupplierPaymentRules.Trimmed(request.Note);
    }

    /// <summary>MSG-GEN-02 (409, <c>field</c> "AccountNumber") when another account of the kind has the provider and number.</summary>
    private async Task<AppError?> DuplicateAsync(CashAccount account, Guid? exceptAccountId, CancellationToken cancellationToken) =>
        await accounts.FindDuplicateAsync(account.Kind, account.ProviderName, account.AccountNumber, exceptAccountId, cancellationToken) is { } existing
            ? AppError.AlreadyExists("Cash account", existing.Id, "AccountNumber")
            : null;

    /// <summary>The number is masked in the audit log, as the receiving bank account is.</summary>
    private static object Snapshot(CashAccount a) => new
    {
        a.Kind, a.BankBin, a.ProviderName, AccountNumber = Masking.ReceivingAccountNumber(a.AccountNumber), a.HolderName, a.Note, a.IsActive,
    };

    private static bool IsVietQr(CashAccount account, ReceivingBankAccount? receiving) =>
        receiving is not null && account.Kind == CashAccountKind.Bank && account.BankBin == receiving.BankBin
        && string.Equals(account.AccountNumber, receiving.AccountNumber, StringComparison.OrdinalIgnoreCase);

    private async Task<CashAccountView> ViewAsync(CashAccount account, CancellationToken cancellationToken) =>
        View(account, (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount);

    private static CashAccountView View(CashAccount a, ReceivingBankAccount? receiving) => new(
        a.Id, a.Kind, a.BankBin, a.ProviderName, a.AccountNumber, a.HolderName, a.Note, a.IsActive, IsVietQr(a, receiving),
        RowVersionGuard.Encode(a.RowVersion));
}
