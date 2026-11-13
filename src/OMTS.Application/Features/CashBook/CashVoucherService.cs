using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.CashBook;

/// <summary>
/// The self-made vouchers of the cash book: money in or out that is not a supplier payment. Everything on a Recorded voucher may be
/// edited, its amount and fund included; one made by mistake is cancelled and no longer counts. The API checks the role: no zone,
/// the Owner and the Accountant. Each save holds the voucher and its audit record.
/// </summary>
public sealed class CashVoucherService(
    ICashVoucherRepository vouchers,
    ICashVoucherTypeRepository types,
    ICashAccountRepository cashAccounts,
    ICashBookQuery query,
    IAccountRepository accounts,
    CashVoucherCodeService codes,
    CashBookLookupService lookups,
    CustomerPaymentService customerPayments,
    ICurrentUser currentUser,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(CashVoucher);

    public async Task<Result<CashVoucherView>> GetAsync(Guid voucherId, CancellationToken cancellationToken) =>
        await vouchers.GetForEditAsync(voucherId, cancellationToken) is { } voucher ? await ViewAsync(voucher, cancellationToken) : AppError.NotFound;

    public async Task<Result<CashVoucherView>> CreateAsync(SaveCashVoucherRequest request, CancellationToken cancellationToken)
    {
        if (request.CountInDebt)
        {
            return await CreateCustomerPaymentAsync(request, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var (type, account) = await ReferencesAsync(request, cancellationToken);
        var invalid = CashBookRules.InvalidVoucher(request, type, account, now);
        var party = await CheckPeopleAsync(request, null, invalid, cancellationToken);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        // The typed code, or one taken once the voucher is valid, so a refused request spends no number.
        var (code, duplicate) = await codes.ForNewAsync(request.Fund!.Value, request.Direction!.Value, request.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var voucher = new CashVoucher { Code = code };
        Apply(voucher, request, type!, account, party);
        voucher.CollectedByAccountId = request.CollectorId ?? currentUser.AccountId;
        vouchers.Add(voucher);
        audit.Record(new AuditEntry("CashVoucher.Recorded", RecordType) { RecordId = voucher.Id, RecordCode = voucher.Code, After = Snapshot(voucher) });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(voucher, cancellationToken);
    }

    /// <summary>
    /// "Tính vào công nợ": the voucher of a customer that counts in the debt is saved as a customer payment (a receipt, with its allocation to the invoices or only off the debt) or as
    /// money paid back to the customer, so the debt, the invoices and the cash book all read the one record. The refusals of the payment (the customer owes nothing, the amount is above
    /// what is owed or owed back) come back as they are. Any fund (Cash, Bank or EWallet); the type is the system type of the direction.
    /// </summary>
    private async Task<Result<CashVoucherView>> CreateCustomerPaymentAsync(SaveCashVoucherRequest request, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        if (request.PartyKind != CashPartyKind.Customer || request.PartyId is null)
        {
            invalid.Add("Party");
        }

        if (request.Direction is not { } direction || !Enum.IsDefined(direction))
        {
            invalid.Add("Direction");
        }

        var account = request.CashAccountId is { } accountId ? await cashAccounts.GetForEditAsync(accountId, cancellationToken) : null;
        if (request.Fund is not { } fundChosen || !Enum.IsDefined(fundChosen) || !CashBookRules.AccountMatchesFund(fundChosen, request.CashAccountId, account))
        {
            invalid.Add("Cash account");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var kind = request.Direction == CashDirection.Receipt ? CustomerPaymentKind.Payment : CustomerPaymentKind.PaidBack;
        var payment = new RecordCustomerPaymentRequest(
            request.PartyId,
            PaymentDate: null,
            request.Amount,
            request.Fund == CashFund.Cash ? CustomerPaymentMethod.Cash : request.Fund == CashFund.EWallet ? CustomerPaymentMethod.EWallet : CustomerPaymentMethod.OtherTransfer,
            CollectedByAccountId: request.CollectorId,
            Note: request.Note,
            PaidAt: request.OccurredAt,
            Code: request.Code,
            CashAccountId: request.Fund == CashFund.Cash ? null : request.CashAccountId,
            Allocations: kind == CustomerPaymentKind.Payment && request.DebtMode == CashDebtMode.DebtOnly ? [] : request.DebtAllocations);
        var saved = await customerPayments.RecordFromCashVoucherAsync(payment, kind, cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved.Error!;
        }

        var p = saved.Value!.Payment;
        var typeId = kind == CustomerPaymentKind.Payment ? CashVoucherTypeIds.ReceiptFromCustomer : CashVoucherTypeIds.PaymentToCustomer;
        var read = await lookups.LoadAsync(
            [typeId], p.CashAccount is { } cash ? [cash.Id] : [], new[] { p.CollectedBy?.Id, p.RecordedBy?.Id }.OfType<Guid>(),
            p.Customer is { } customer ? [(CashPartyKind.Customer, customer.Id)] : [], cancellationToken);
        return new CashVoucherView(
            p.Id, p.Code, request.Direction!.Value, request.Fund!.Value, read.Account(p.CashAccount?.Id), read.Type(typeId), p.PaidAt, p.Amount, read.Person(p.CollectedBy?.Id),
            p.Customer is { } party ? read.Party(CashPartyKind.Customer, party.Id, party.Name) : null, p.Note, false, CashEntryStatus.Recorded, null, null,
            p.ConfirmedAt ?? p.PaidAt, read.Person(p.RecordedBy?.Id), null, p.RowVersion,
            kind == CustomerPaymentKind.Payment ? CashSource.CustomerPayment : CashSource.CustomerPaidBack, new CashSourceDocument("CustomerPayment", p.Id, p.Code));
    }

    public async Task<Result<CashVoucherView>> UpdateAsync(Guid voucherId, SaveCashVoucherRequest request, CancellationToken cancellationToken)
    {
        var voucher = await vouchers.GetForEditAsync(voucherId, cancellationToken);
        if (voucher is null)
        {
            return AppError.NotFound;
        }

        if (voucher.CancelledAt is not null)
        {
            return CancelledError(voucher);
        }

        if (await rowVersions.CheckAsync(voucher, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var (type, account) = await ReferencesAsync(request, cancellationToken);
        var invalid = CashBookRules.InvalidVoucher(request, type, account, timeProvider.GetUtcNow(), voucher.TypeId, voucher.CashAccountId);
        var party = await CheckPeopleAsync(request, voucher, invalid, cancellationToken);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var (code, duplicate) = await codes.ForEditAsync(request.Code, voucher.Id, voucher.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var before = Snapshot(voucher);
        voucher.Code = code;
        Apply(voucher, request, type!, account, party);
        voucher.CollectedByAccountId = request.CollectorId ?? voucher.CollectedByAccountId;
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(voucher));
        if (changedAfter is not null)
        {
            unitOfWork.MarkChanged(voucher);
            audit.Record(new AuditEntry("CashVoucher.Edited", RecordType)
            {
                RecordId = voucher.Id, RecordCode = voucher.Code, Before = changedBefore, After = changedAfter,
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await ViewAsync(voucher, cancellationToken);
    }

    /// <summary>A voucher made by mistake: Recorded → Cancelled with an optional reason; it no longer counts in any balance.</summary>
    public async Task<Result<CashVoucherView>> CancelAsync(Guid voucherId, CancelCashVoucherRequest request, CancellationToken cancellationToken)
    {
        var voucher = await vouchers.GetForEditAsync(voucherId, cancellationToken);
        if (voucher is null)
        {
            return AppError.NotFound;
        }

        if (voucher.CancelledAt is not null)
        {
            return CancelledError(voucher);
        }

        var reason = SupplierPaymentRules.Trimmed(request.Reason);
        if (reason?.Length > CashBookRules.MaxReasonLength)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(voucher, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        voucher.CancelledAt = timeProvider.GetUtcNow();
        voucher.CancelReason = reason;
        unitOfWork.MarkChanged(voucher);
        audit.Record(new AuditEntry("CashVoucher.Cancelled", RecordType)
        {
            RecordId = voucher.Id,
            RecordCode = voucher.Code,
            Before = new { Status = CashEntryStatus.Recorded },
            After = new { Status = CashEntryStatus.Cancelled },
            Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(voucher, cancellationToken);
    }

    private static AppError CancelledError(CashVoucher voucher) =>
        AppError.Conflict(UserMessages.WrongStatus($"Cash voucher {voucher.Code}", CashEntryStatus.Cancelled.ToString()));

    private async Task<(CashVoucherType? Type, CashAccount? Account)> ReferencesAsync(SaveCashVoucherRequest request, CancellationToken cancellationToken) => (
        request.TypeId is { } typeId ? await types.GetForEditAsync(typeId, cancellationToken) : null,
        request.CashAccountId is { } accountId ? await cashAccounts.GetForEditAsync(accountId, cancellationToken) : null);

    /// <summary>
    /// Adds "Collector" and "Party" to <paramref name="invalid"/> when the account is not an Active Owner or staff account, or the
    /// party named does not exist; the people of a voucher being edited may stay as they are. Returns the party found.
    /// </summary>
    private async Task<CashPartyView?> CheckPeopleAsync(SaveCashVoucherRequest request, CashVoucher? stored, List<string> invalid, CancellationToken cancellationToken)
    {
        if (request.CollectorId is { } collectorId && collectorId != stored?.CollectedByAccountId
            && !(await accounts.ListActiveInternalAsync(cancellationToken)).Any(a => a.Id == collectorId))
        {
            invalid.Add("Collector");
        }

        if (request.PartyKind is not { } kind || request.PartyId is not { } partyId || invalid.Contains("Party"))
        {
            return null;
        }

        var found = (await query.PartiesAsync([(kind, partyId)], cancellationToken)).GetValueOrDefault((kind, partyId));
        if (found is null)
        {
            invalid.Add("Party");
        }

        return found;
    }

    /// <summary>
    /// Sets everything a request may change. The name of the party is the one of its record, or the one stored when the voucher
    /// still names the same party, so a renamed party does not rewrite old vouchers.
    /// </summary>
    private void Apply(CashVoucher voucher, SaveCashVoucherRequest request, CashVoucherType type, CashAccount? account, CashPartyView? party)
    {
        var occurredAt = request.OccurredAt!.Value.ToOffset(BusinessClock.UtcOffset);
        var samePartyAsStored = voucher.PartyKind == request.PartyKind && voucher.PartyId == request.PartyId && voucher.PartyName is not null;
        voucher.Direction = request.Direction!.Value;
        voucher.Fund = request.Fund!.Value;
        voucher.CashAccountId = account?.Id;
        voucher.CashAccount = account;
        voucher.TypeId = type.Id;
        voucher.Type = type;
        voucher.OccurredAt = occurredAt;
        voucher.VoucherDate = BusinessClock.ToBusinessDate(occurredAt);
        voucher.Amount = request.Amount!.Value;
        voucher.PartyKind = party?.Kind;
        voucher.PartyId = party?.Id;
        voucher.PartyName = party is null ? null : samePartyAsStored ? voucher.PartyName : party.Name;
        voucher.Note = SupplierPaymentRules.Trimmed(request.Note);
        voucher.CountsInResult = request.CountsInResult ?? type.CountsInResult;
    }

    private static object Snapshot(CashVoucher v) => new
    {
        v.Code, v.Direction, v.Fund, v.CashAccountId, v.TypeId, v.OccurredAt, v.VoucherDate, v.Amount, v.CollectedByAccountId,
        v.PartyKind, v.PartyId, v.PartyName, v.Note, v.CountsInResult,
    };

    private async Task<CashVoucherView> ViewAsync(CashVoucher v, CancellationToken cancellationToken)
    {
        var partyKeys = v.PartyKind is { } kind && v.PartyId is { } partyId ? [(kind, partyId)] : Array.Empty<(CashPartyKind, Guid)>();
        var read = await lookups.LoadAsync(
            [v.TypeId], v.CashAccountId is { } accountId ? [accountId] : [],
            new[] { v.CollectedByAccountId, v.CreatedBy }.Where(id => id is not null).Select(id => id!.Value), partyKeys, cancellationToken);
        return new CashVoucherView(
            v.Id, v.Code, v.Direction, v.Fund, read.Account(v.CashAccountId), read.Type(v.TypeId), v.OccurredAt, v.Amount, read.Person(v.CollectedByAccountId),
            read.Party(v.PartyKind, v.PartyId, v.PartyName), v.Note, v.CountsInResult, v.CancelledAt is null ? CashEntryStatus.Recorded : CashEntryStatus.Cancelled,
            v.CancelledAt, v.CancelReason, v.CreatedAt, read.Person(v.CreatedBy), v.UpdatedAt, RowVersionGuard.Encode(v.RowVersion));
    }
}
