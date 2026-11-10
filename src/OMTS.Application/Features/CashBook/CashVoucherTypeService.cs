using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.CashBook;

/// <summary>
/// The voucher types of the cash book. The system types are seeded and never change (MSG-CASH-02); a type the Accountant made may
/// be edited, and deleted when no voucher has it, otherwise it is hidden: no longer offered, still named on the vouchers that have it.
/// A user type is always one that may be chosen by hand.
/// </summary>
public sealed class CashVoucherTypeService(
    ICashVoucherTypeRepository types,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(CashVoucherType);

    public async Task<IReadOnlyList<CashVoucherTypeView>> ListAsync(CashDirection? direction, CancellationToken cancellationToken) =>
        (await types.ListActiveAsync(direction, cancellationToken)).Select(View).ToList();

    public async Task<Result<CashVoucherTypeView>> CreateAsync(SaveCashVoucherTypeRequest request, CancellationToken cancellationToken)
    {
        var invalid = CashBookRules.InvalidType(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var type = new CashVoucherType { IsSystem = false, Manual = true, IsActive = true };
        Apply(type, request);
        if (await DuplicateAsync(type, null, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        types.Add(type);
        audit.Record(new AuditEntry("CashVoucherType.Created", RecordType) { RecordId = type.Id, After = Snapshot(type) });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(type);
    }

    public async Task<Result<CashVoucherTypeView>> UpdateAsync(Guid typeId, SaveCashVoucherTypeRequest request, CancellationToken cancellationToken)
    {
        var type = await types.GetForEditAsync(typeId, cancellationToken);
        if (type is null || !type.IsActive)
        {
            return AppError.NotFound;
        }

        if (type.IsSystem)
        {
            return AppError.Conflict(UserMessages.CashSystemVoucherType);
        }

        if (await rowVersions.CheckAsync(type, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var invalid = CashBookRules.InvalidType(request);
        if (invalid.Count == 0 && (request.Direction != type.Direction || request.Category != type.Category)
            && await types.IsUsedAsync(type.Id, cancellationToken))
        {
            // The vouchers of the type would no longer match its direction.
            invalid.Add(request.Direction != type.Direction ? "Direction" : "Category");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var before = Snapshot(type);
        Apply(type, request);
        if (await DuplicateAsync(type, type.Id, cancellationToken) is { } duplicate)
        {
            await unitOfWork.ReloadAsync(type, cancellationToken);
            return duplicate;
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(type));
        if (changedAfter is not null)
        {
            unitOfWork.MarkChanged(type);
            audit.Record(new AuditEntry("CashVoucherType.Edited", RecordType) { RecordId = type.Id, Before = changedBefore, After = changedAfter });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return View(type);
    }

    /// <summary>Deletes a type no voucher has, otherwise hides it; either way it is no longer listed.</summary>
    public async Task<Result<Done>> DeleteAsync(Guid typeId, CancellationToken cancellationToken)
    {
        var type = await types.GetForEditAsync(typeId, cancellationToken);
        if (type is null || !type.IsActive)
        {
            return AppError.NotFound;
        }

        if (type.IsSystem)
        {
            return AppError.Conflict(UserMessages.CashSystemVoucherType);
        }

        if (await types.IsUsedAsync(type.Id, cancellationToken))
        {
            type.IsActive = false;
            unitOfWork.MarkChanged(type);
            audit.Record(new AuditEntry("CashVoucherType.Hidden", RecordType)
            {
                RecordId = type.Id, Before = new { IsActive = true }, After = new { IsActive = false },
            });
        }
        else
        {
            types.Remove(type);
            audit.Record(new AuditEntry("CashVoucherType.Deleted", RecordType) { RecordId = type.Id, Before = Snapshot(type) });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    private static void Apply(CashVoucherType type, SaveCashVoucherTypeRequest request)
    {
        type.Direction = request.Direction!.Value;
        type.Category = request.Category!.Value;
        type.Name = request.Name!.Trim();
        type.Description = SupplierPaymentRules.Trimmed(request.Description);
        type.CountsInResult = request.CountsInResult;
    }

    /// <summary>MSG-GEN-02 (409, <c>field</c> "Name") when another listed type of the direction has the name; a hidden one does not count.</summary>
    private async Task<AppError?> DuplicateAsync(CashVoucherType type, Guid? exceptTypeId, CancellationToken cancellationToken) =>
        await types.FindByNameAsync(type.Direction, type.Name, exceptTypeId, cancellationToken) is { } existing
            ? AppError.AlreadyExists("Cash voucher type", existing.Id, "Name")
            : null;

    private static object Snapshot(CashVoucherType t) => new { t.Direction, t.Category, t.Name, t.Description, t.CountsInResult };

    private static CashVoucherTypeView View(CashVoucherType t) => new(
        t.Id, t.Direction, t.Category, t.Name, t.Description, t.CountsInResult, t.IsSystem, t.Manual, RowVersionGuard.Encode(t.RowVersion));
}
