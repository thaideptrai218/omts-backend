using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>
/// People hired outside the system for the day of a work-day plan (the "thuê ngoài" lines): a number of people at a rate per session. They count towards the people needed, so adding, changing or removing a line settles the plan.
/// Paying a line (Owner and Accountant, the right of paying wages) makes one cash voucher of the "Chi nhân công thuê ngoài" type, People times the rate, outside payroll and its pay period lock. The cost goes to the raft of the plan, or
/// to its zone for a nursery plan (<c>LaborCostService</c>).
/// </summary>
public sealed class WorkDayPlanOutsiderService(
    IWorkDayPlanRepository plans,
    ICashVoucherRepository vouchers,
    ICashAccountRepository cashAccounts,
    ICashVoucherTypeRepository voucherTypes,
    IAccountRepository people,
    CashVoucherCodeService codes,
    WorkDayPlanAccess access,
    WorkDayPlanViews views,
    WorkDayPlanSettler settler,
    RowVersionGuard rowVersions,
    ICurrentUser user,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(WorkDayPlan);

    public async Task<Result<WorkDayPlanItem>> AddAsync(Guid planId, SaveOutsiderHireRequest request, CancellationToken cancellationToken)
    {
        if (Invalid(request) is { } invalid)
        {
            return invalid;
        }

        var loaded = await LoadAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        var hire = new PlanOutsiderHire
        {
            Plan = plan, PlanId = plan.Id, People = request.People!.Value, RatePerSession = request.RatePerSession!.Value, PayeeName = Clean(request.PayeeName), Note = Clean(request.Note),
        };
        plans.AddOutsider(hire);
        if (!plan.OutsiderHires.Contains(hire))
        {
            plan.OutsiderHires.Add(hire);
        }

        audit.Record(new AuditEntry("WorkDayPlan.OutsiderAdded", RecordType)
        {
            RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, After = new { hire.People, hire.RatePerSession, hire.PayeeName },
        });
        await settler.SettleAsync(plan, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    /// <summary>Changes a line that is not paid yet; a paid line is a voucher in the cash book and stays as it is (409).</summary>
    public async Task<Result<WorkDayPlanItem>> UpdateAsync(Guid planId, Guid hireId, SaveOutsiderHireRequest request, CancellationToken cancellationToken)
    {
        if (Invalid(request) is { } invalid)
        {
            return invalid;
        }

        var loaded = await LoadAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        var hire = plan.OutsiderHires.FirstOrDefault(h => h.Id == hireId);
        if (hire is null)
        {
            return AppError.NotFound;
        }

        if (hire.IsPaid)
        {
            return WorkDayPlanStaffingService.Refused("Dòng thuê ngoài đã chi nên không thể sửa.", "OUTSIDER_PAID");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(hire, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = new { hire.People, hire.RatePerSession, hire.PayeeName, hire.Note };
        (hire.People, hire.RatePerSession, hire.PayeeName, hire.Note) = (request.People!.Value, request.RatePerSession!.Value, Clean(request.PayeeName), Clean(request.Note));
        unitOfWork.MarkChanged(hire);
        audit.Record(new AuditEntry("WorkDayPlan.OutsiderChanged", RecordType)
        {
            RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, Before = before, After = new { hire.People, hire.RatePerSession, hire.PayeeName, hire.Note },
        });
        await settler.SettleAsync(plan, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    public async Task<Result<WorkDayPlanItem>> RemoveAsync(Guid planId, Guid hireId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        var hire = plan.OutsiderHires.FirstOrDefault(h => h.Id == hireId);
        if (hire is null)
        {
            return AppError.NotFound;
        }

        if (hire.IsPaid)
        {
            return WorkDayPlanStaffingService.Refused("Dòng thuê ngoài đã chi nên không thể xóa.", "OUTSIDER_PAID");
        }

        plan.OutsiderHires.Remove(hire);
        plans.RemoveOutsider(hire);
        audit.Record(new AuditEntry("WorkDayPlan.OutsiderRemoved", RecordType)
        {
            RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, Before = new { hire.People, hire.RatePerSession, hire.PayeeName },
        });
        await settler.SettleAsync(plan, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    /// <summary>
    /// Pays the line: a payment voucher of People times the rate (whole VND above zero), party "other", dated <c>paidAt</c> (not in the future; default now). 400 MSG-GEN-01 for a wrong <c>Paid at</c>, <c>Method</c>,
    /// <c>Cash account</c>, <c>Paid by</c> or <c>Amount</c>; 409 when the line is paid already or the plan is cancelled. The Owner and the Accountant pay (the API checks PAYROLL Create); a Zone Manager does not.
    /// </summary>
    public async Task<Result<WorkDayPlanItem>> PayAsync(Guid planId, Guid hireId, PayOutsiderHireRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var paidAt = request.PaidAt ?? now;
        var invalid = new List<string>();
        if (!SupplierPaymentRules.NotInFuture(paidAt, now))
        {
            invalid.Add("Paid at");
        }

        if (request.Method is not (PaymentMethod.Cash or PaymentMethod.BankTransfer))
        {
            invalid.Add("Method");
        }

        if (request.Note?.Trim().Length > WorkDayPlanRules.MaxNoteLength)
        {
            invalid.Add("Note");
        }

        if (!await AccountAllowedAsync(request.Method, request.CashAccountId, cancellationToken))
        {
            invalid.Add("Cash account");
        }

        if (request.PaidById is { } payer && payer != user.AccountId && !(await people.ListActiveInternalAsync(cancellationToken)).Any(a => a.Id == payer))
        {
            invalid.Add("Paid by");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        // Whoever may pay wages (the Owner, the Accountant) pays the line of any plan that is not cancelled, whether or not they see the plans of the zone.
        var who = await access.CurrentAsync(cancellationToken);
        if (who is null || !WorkDayPlanAccess.MayPayOutsiders(who))
        {
            return AppError.Forbidden;
        }

        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null)
        {
            return AppError.NotFound;
        }

        if (plan.Status == WorkDayPlanStatus.Cancelled)
        {
            return WorkDayPlanStaffingService.Refused($"Kế hoạch {plan.Code} đã hủy nên không thể chi nhân công thuê ngoài.", "PLAN_CLOSED");
        }

        var hire = plan.OutsiderHires.FirstOrDefault(h => h.Id == hireId);
        if (hire is null)
        {
            return AppError.NotFound;
        }

        if (hire.IsPaid)
        {
            return WorkDayPlanStaffingService.Refused("Dòng thuê ngoài đã được chi.", "OUTSIDER_PAID");
        }

        var amount = hire.Amount;
        if (amount < 1 || amount > SupplierPaymentRules.MaxAmount || amount != decimal.Truncate(amount))
        {
            return AppError.InvalidFields(["Amount"]);
        }

        var type = (await voucherTypes.GetByIdsAsync([CashVoucherTypeIds.PaymentOutsiderLabour], cancellationToken)).GetValueOrDefault(CashVoucherTypeIds.PaymentOutsiderLabour);
        var method = request.Method!.Value;
        var fund = CashBookRules.FundOfPayment(method);
        var occurredAt = paidAt.ToOffset(BusinessClock.UtcOffset);
        var paidBy = request.PaidById ?? user.AccountId!.Value;
        var voucher = new CashVoucher
        {
            Code = await codes.NextAsync(fund, CashDirection.Payment, cancellationToken),
            Direction = CashDirection.Payment,
            Fund = fund,
            CashAccountId = fund == CashFund.Bank ? request.CashAccountId : null,
            TypeId = CashVoucherTypeIds.PaymentOutsiderLabour,
            OccurredAt = occurredAt,
            VoucherDate = BusinessClock.ToBusinessDate(occurredAt),
            Amount = amount,
            CollectedByAccountId = paidBy,
            PartyKind = CashPartyKind.Other,
            PartyName = hire.PayeeName ?? "Nhân công thuê ngoài",
            Note = VoucherNote(plan, hire, request.Note),
            CountsInResult = type?.CountsInResult ?? true,
        };
        vouchers.Add(voucher);
        hire.CashVoucherId = voucher.Id;
        hire.PaidAt = occurredAt;
        hire.PaidByAccountId = paidBy;
        unitOfWork.MarkChanged(hire);
        unitOfWork.MarkChanged(plan);
        audit.Record(new AuditEntry("WorkDayPlan.OutsiderPaid", RecordType)
        {
            RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, After = new { hire.People, hire.RatePerSession, Amount = amount, VoucherId = voucher.Id, VoucherCode = voucher.Code },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    // ---- Building ----

    /// <summary>The plan with its lines when the caller sees it (404), may staff it (403) and it is not over (409).</summary>
    private async Task<Result<WorkDayPlan>> LoadAsync(Guid planId, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        if (who is null)
        {
            return AppError.Forbidden;
        }

        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !WorkDayPlanAccess.MaySee(who, plan))
        {
            return AppError.NotFound;
        }

        if (!WorkDayPlanAccess.MayStaff(who, plan))
        {
            return AppError.Forbidden;
        }

        return WorkDayPlanRules.Open(plan.Status)
            ? plan
            : WorkDayPlanStaffingService.Refused($"Kế hoạch {plan.Code} đang ở trạng thái {WorkDayPlanRules.StatusName(plan.Status)} nên không thể đổi nhân công thuê ngoài.", "PLAN_CLOSED");
    }

    private static AppError? Invalid(SaveOutsiderHireRequest request)
    {
        var invalid = new List<string>();
        if (request.People is not (>= 1 and <= WorkDayPlanRules.MaxRequiredPeople))
        {
            invalid.Add("People");
        }

        if (request.RatePerSession is not { } rate || rate < 0 || rate > SupplierPaymentRules.MaxAmount || rate != decimal.Truncate(rate))
        {
            invalid.Add("Rate per session");
        }

        if (request.PayeeName?.Trim().Length > WorkDayPlanRules.MaxPayeeLength)
        {
            invalid.Add("Payee name");
        }

        if (request.Note?.Trim().Length > 1000)
        {
            invalid.Add("Note");
        }

        return invalid.Count > 0 ? AppError.InvalidFields(invalid) : null;
    }

    private async Task<bool> AccountAllowedAsync(PaymentMethod? method, Guid? accountId, CancellationToken cancellationToken)
    {
        if (accountId is null)
        {
            return true;
        }

        if (method != PaymentMethod.BankTransfer)
        {
            return false;
        }

        var account = (await cashAccounts.GetByIdsAsync([accountId.Value], cancellationToken)).GetValueOrDefault(accountId.Value);
        return account is { IsActive: true, Kind: CashAccountKind.Bank };
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string VoucherNote(WorkDayPlan plan, PlanOutsiderHire hire, string? note)
    {
        var text = $"Chi nhân công thuê ngoài, kế hoạch {plan.Code} ngày {plan.WorkDate:dd/MM/yyyy}: {hire.People} người x {hire.RatePerSession:N0} đ";
        return Clean(note) is { } extra ? $"{text}. {extra}" : text;
    }
}
