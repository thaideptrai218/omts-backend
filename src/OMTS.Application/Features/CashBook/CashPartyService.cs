using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.CashBook;

/// <summary>The payers and receivers a voucher names (customers, suppliers, employees, and others made on the spot) and the people who collect or pay.</summary>
public sealed class CashPartyService(
    ICashBookQuery query,
    ICashPartyRepository parties,
    IAccountRepository accounts,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public const int MaxTake = 50;

    public async Task<IReadOnlyList<CashPartyView>> SearchAsync(CashPartyKind kind, string? search, int take, CancellationToken cancellationToken) =>
        await query.SearchPartiesAsync(kind, SupplierPaymentRules.Trimmed(search), Math.Clamp(take, 1, MaxTake), cancellationToken);

    public async Task<Result<CashPartyView>> CreateAsync(CreateCashPartyRequest request, CancellationToken cancellationToken)
    {
        var invalid = CashBookRules.InvalidParty(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var party = new CashParty
        {
            Name = request.Name!.Trim(),
            PhoneNumber = SupplierPaymentRules.Trimmed(request.PhoneNumber),
            Address = SupplierPaymentRules.Trimmed(request.Address),
            Area = SupplierPaymentRules.Trimmed(request.Area),
            Ward = SupplierPaymentRules.Trimmed(request.Ward),
            Note = SupplierPaymentRules.Trimmed(request.Note),
        };
        parties.Add(party);
        audit.Record(new AuditEntry("CashParty.Created", nameof(CashParty))
        {
            RecordId = party.Id, After = new { party.Name, party.PhoneNumber, party.Address, party.Area, party.Ward, party.Note },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CashPartyView(party.Id, CashPartyKind.Other, null, party.Name, party.PhoneNumber);
    }

    /// <summary>Who may collect or pay the money of a voucher: the Active Owner and staff accounts, by name (as the payers of a supplier payment).</summary>
    public async Task<IReadOnlyList<AccountRef>> CollectorsAsync(CancellationToken cancellationToken) =>
        (await accounts.ListActiveInternalAsync(cancellationToken)).Select(a => new AccountRef(a.Id, a.DisplayName)).ToList();
}
