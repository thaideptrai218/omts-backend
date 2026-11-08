using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Requests;
using OMTS.Application.Features.Sales.Requests.Dtos;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using S = OMTS.Domain.Constants.StatusIds.PurchaseRequest;

namespace OMTS.Application.Features.Workforce;

/// <summary>Yêu cầu 06/11/2026: kế hoạch qua người phụ trách; quản lý nhận/từ chối trước khi phân công.</summary>
public sealed class WorkDayPlanService(IWorkDayPlanRepository plans,
    IRaftRepository rafts, IWorkforceRepository workforce, IPurchaseRequestRepository purchases,
    IAccountRepository accounts, ICurrentUser user, PermissionService permissions, WorkPlanAccessService access,
    PurchaseStaffingService staffing, WorkforceService work, RowVersionGuard versions,
    AuditLogService audit, INotificationDispatcher notifications, IUnitOfWork uow, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);
    public async Task<Result<IReadOnlyList<WorkDayPlanView>>> ListAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var who = await access.CurrentAsync(ct);
        if (who is null || !who.Allows(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone, null)) return AppError.Forbidden;
        if (from == default || to < from || to.DayNumber - from.DayNumber > 62) return AppError.Invalid("Work date");
        var result = new List<WorkDayPlanView>();
        foreach (var p in await plans.SearchAsync(from, to, who.IsCooperativeOwner ? null : who.AccountId, ct))
            if (await access.CanViewAsync(p, ct)) result.Add(await ViewAsync(p, ct));
        return Result<IReadOnlyList<WorkDayPlanView>>.Success(result);
    }
    public async Task<Result<WorkDayPlanView>> GetAsync(Guid id, CancellationToken ct)
    {
        var p = await plans.GetAsync(id, ct);
        if (p is null) return AppError.NotFound;
        return await access.CanViewAsync(p, ct) ? await ViewAsync(p, ct) : AppError.Forbidden;
    }
    public async Task<Result<WorkDayPlanView?>> ForPurchaseAsync(Guid id, CancellationToken ct)
    {
        var who = await access.CurrentAsync(ct);
        var p = await plans.ForPurchaseAsync(id, ct);
        if (p is null) return who?.IsCooperativeOwner == true ? Result<WorkDayPlanView?>.Success(null) : AppError.Forbidden;
        return await access.CanViewAsync(p, ct) ? await ViewAsync(p, ct) : AppError.Forbidden;
    }
    public async Task<Result<IReadOnlyList<WorkPlanResponsible>>> ResponsiblesAsync(Guid raftId, CancellationToken ct)
    {
        var who = await access.CurrentAsync(ct);
        if (who?.IsCooperativeOwner != true) return AppError.Forbidden;
        var raft = await rafts.GetForEditAsync(raftId, ct);
        if (raft?.StatusId != StatusIds.Usage.InUse || raft.Zone.StatusId != StatusIds.Usage.InUse) return AppError.Invalid("Raft");
        var owner = await accounts.GetByIdAsync(who.AccountId, ct);
        var result = new List<WorkPlanResponsible> { new(who.AccountId, owner!.DisplayName, true) };
        foreach (var candidate in await plans.ResponsibleCandidatesAsync(ct))
            if ((await permissions.GetAccessAsync(candidate.Id, ct)) is { } rights &&
                rights.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.ZoneScoped, raft.ZoneId) &&
                rights.Allows(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.ZoneScoped, raft.ZoneId))
                result.Add(new(candidate.Id, candidate.DisplayName, false));
        return Result<IReadOnlyList<WorkPlanResponsible>>.Success(result);
    }
    public async Task<Result<WorkDayPlanView>> SaveAsync(Guid? id, SaveWorkDayPlan input, CancellationToken ct)
    {
        var who = await access.CurrentAsync(ct);
        if (who?.IsCooperativeOwner != true) return AppError.Forbidden;
        if (input.ClientOperationId == Guid.Empty || input.WorkDate < Today || input.WorkDate == default || input.Note?.Length > 500)
            return AppError.Invalid("Work plan");
        var raft = await rafts.GetForEditAsync(input.RaftId, ct);
        var job = await workforce.JobAsync(input.JobTypeId, ct);
        if (raft?.StatusId != StatusIds.Usage.InUse || raft.Zone.StatusId != StatusIds.Usage.InUse || job?.StatusId != StatusIds.Usage.InUse)
            return AppError.Invalid("Job type");
        var responsible = await accounts.GetByIdAsync(input.ResponsibleAccountId, ct);
        var responsibleAccess = responsible is null ? null : await permissions.GetAccessAsync(responsible.Id, ct);
        if (responsible?.StatusId != StatusIds.Account.Active ||
            responsible.Id != who.AccountId && (responsible.SubjectType != SubjectType.Staff ||
                responsibleAccess is null ||
                !responsibleAccess.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.ZoneScoped, raft.ZoneId) ||
                !responsibleAccess.Allows(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.ZoneScoped, raft.ZoneId)))
            return AppError.Invalid("Plan responsible");
        WorkDayPlan? p;
        if (id is { } planId)
        {
            p = await plans.GetAsync(planId, ct);
            if (p is null) return AppError.NotFound;
            if (await versions.CheckAsync(p, input.RowVersion, ct) is { } stale) return stale;
            if (p.Assignments.Any(a => a.CancelledAt is null) || p.PurchaseRequest?.ShiftInvitations.Count > 0)
                return AppError.Invalid("Work plan already assigned");
            if (p.PurchaseRequestId != input.PurchaseRequestId) return AppError.Invalid("Purchase request");
        }
        else
        {
            p = await plans.FindOperationAsync(who.AccountId, input.ClientOperationId, ct);
            if (p is not null)
            {
                if (p.RaftId != input.RaftId || p.JobTypeId != input.JobTypeId || p.WorkDate != input.WorkDate ||
                    p.ResponsibleAccountId != input.ResponsibleAccountId || p.PurchaseRequestId != input.PurchaseRequestId ||
                    p.Note != input.Note?.Trim() || p.PurchaseRequest is { } old &&
                    (old.PlannedCages != input.Cages || old.PlannedPickupDate != input.PickupDate || old.CagesPerEmployeePerDay != input.CagesPerEmployeePerDay))
                    return AppError.Invalid("Client operation");
                return await ViewAsync(p, ct);
            }
            if (input.PurchaseRequestId is { } purchaseId && await plans.ForPurchaseAsync(purchaseId, ct) is { } existing)
                return AppError.AlreadyExists("Work plan", existing.Id);
            p = new WorkDayPlan { CreatorAccountId = who.AccountId, CreatorAccount = (await accounts.GetByIdAsync(who.AccountId, ct))!, ClientOperationId = input.ClientOperationId };
        }
        PurchaseRequest? order = null;
        if (input.PurchaseRequestId is { } orderId)
        {
            order = await purchases.GetAsync(orderId, ct);
            if (order is null) return AppError.NotFound;
            if (order.StatusId is not (S.AwaitingResponse or S.AwaitingStaff) || order.ShiftInvitations.Count > 0) return AppError.Invalid("Purchase request status");
            if (input.Cages is null or <= 0 || input.Cages > order.RequestedCages || input.CagesPerEmployeePerDay is null or <= 0 || input.PickupDate is null || input.PickupDate < input.WorkDate)
                return AppError.Invalid("Staffing capacity");
            order.PlannedCages = input.Cages; order.WorkDate = input.WorkDate; order.PlannedPickupDate = input.PickupDate;
            order.CagesPerEmployeePerDay = input.CagesPerEmployeePerDay; order.StaffingNote = input.Note?.Trim(); order.StatusId = S.AwaitingStaff;
            uow.MarkChanged(order);
        }
        p.RaftId = raft.Id; p.Raft = raft; p.JobTypeId = job.Id; p.JobType = job; p.WorkDate = input.WorkDate;
        p.ResponsibleAccountId = responsible.Id; p.ResponsibleAccount = responsible; p.PurchaseRequestId = order?.Id; p.PurchaseRequest = order;
        p.Note = input.Note?.Trim(); p.ResponseReason = null;
        p.Approved = responsible.Id == who.AccountId ? true : null;
        p.RespondedAt = p.Approved == true ? clock.GetUtcNow() : null;
        if (id is null) plans.Add(p); else uow.MarkChanged(p);
        audit.Record(new AuditEntry(id is null ? "WorkDayPlan.Created" : "WorkDayPlan.Updated", nameof(WorkDayPlan))
        { RecordId = p.Id, ZoneId = raft.ZoneId, After = new { p.WorkDate, p.RaftId, p.JobTypeId, p.ResponsibleAccountId, p.PurchaseRequestId } });
        if (p.Approved is null)
            await notifications.NotifyAsync([responsible.Id], "WORK_PLAN_REQUESTED", "Kế hoạch ngày làm cần xác nhận",
                $"{input.WorkDate:dd/MM/yyyy}: {job.Name}, bè {raft.Code}. Vui lòng nhận hoặc từ chối kế hoạch.", nameof(WorkDayPlan), p.Id.ToString(), false, ct);
        await uow.SaveChangesAsync(ct);
        return await ViewAsync(p, ct);
    }
    public async Task<Result<WorkDayPlanView>> RespondAsync(Guid id, RespondWorkDayPlan input, CancellationToken ct)
    {
        var p = await plans.GetAsync(id, ct);
        if (p is null) return AppError.NotFound;
        if (p.ResponsibleAccountId != user.AccountId || !await access.CanViewAsync(p, ct)) return AppError.Forbidden;
        if (p.Approved is not null) return AppError.Invalid("Work plan already answered");
        if (p.WorkDate < Today || input.Reason?.Length > 500 || !input.Approved && string.IsNullOrWhiteSpace(input.Reason)) return AppError.Invalid("Response");
        if (p.PurchaseRequest?.StatusId is S.Cancelled or S.Declined) return AppError.Invalid("Purchase request status");
        if (await versions.CheckAsync(p, input.RowVersion, ct) is { } stale) return stale;
        var who = await access.CurrentAsync(ct);
        if (!who!.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.ZoneScoped, p.Raft.ZoneId)) return AppError.Forbidden;
        p.Approved = input.Approved; p.RespondedAt = clock.GetUtcNow(); p.ResponseReason = input.Reason?.Trim();
        audit.Record(new AuditEntry("WorkDayPlan.Responded", nameof(WorkDayPlan))
        { RecordId = p.Id, ZoneId = p.Raft.ZoneId, After = new { p.Approved, p.ResponseReason } });
        await notifications.NotifyAsync([p.CreatorAccountId], "WORK_PLAN_RESPONDED", "Quản lý phản hồi kế hoạch",
            $"{p.ResponsibleAccount.DisplayName} {(input.Approved ? "đồng ý" : "từ chối")} kế hoạch {p.WorkDate:dd/MM/yyyy}. {p.ResponseReason}", nameof(WorkDayPlan), p.Id.ToString(), false, ct);
        await uow.SaveChangesAsync(ct);
        return await ViewAsync(p, ct);
    }
    public async Task<Result<IReadOnlyList<StaffingEmployeeOption>>> EmployeesAsync(Guid id, CancellationToken ct)
    {
        var p = await plans.GetAsync(id, ct);
        if (p is null) return AppError.NotFound;
        if (!await access.CanAssignAsync(p, ct)) return AppError.Forbidden;
        if (p.PurchaseRequestId is { } orderId) return await staffing.EmployeesAsync(p.WorkDate, ct, orderId);
        var options = new List<StaffingEmployeeOption>();
        foreach (var e in await purchases.StaffingEmployeesAsync(ct))
        {
            var reason = e.Account is not { StatusId: StatusIds.Account.Active, SubjectType: SubjectType.Staff } ? "Chưa có tài khoản nhân viên đang hoạt động"
                : await workforce.HasApprovedLeaveAsync(e.Id, p.WorkDate, ct) ? "Đã duyệt nghỉ trong ngày này"
                : await workforce.EmployeeAssignmentAsync(e.Id, p.WorkDate, ct) is not null || await purchases.HasShiftReservationAsync(e.Id, p.WorkDate, null, ct) ? "Đã có ca khác trong ngày" : null;
            options.Add(new(e.Id, e.FullName, reason is null, reason));
        }
        return Result<IReadOnlyList<StaffingEmployeeOption>>.Success(options);
    }
    public async Task<Result<WorkDayPlanView>> AssignAsync(Guid id, AssignWorkDayPlan input, CancellationToken ct)
    {
        var p = await plans.GetAsync(id, ct);
        if (p is null) return AppError.NotFound;
        if (!await access.CanAssignAsync(p, ct)) return AppError.Forbidden;
        if (await versions.CheckAsync(p, input.RowVersion, ct) is { } stale) return stale;
        if (p.PurchaseRequest is { } order)
        {
            var result = await staffing.PlanAsync(order.Id, new PlanPurchaseStaffing(order.PlannedCages!.Value, p.WorkDate,
                order.PlannedPickupDate!.Value, order.CagesPerEmployeePerDay!.Value, input.EmployeeIds, order.StaffingNote, RowVersionGuard.Encode(order.RowVersion)), ct);
            if (!result.IsSuccess) return result.Error!;
        }
        else
        {
            var result = await work.AssignAsync(new AssignWorkRequest(p.RaftId, p.JobTypeId, p.WorkDate, input.EmployeeIds, false, p.Id), ct);
            if (!result.IsSuccess) return result.Error!;
        }
        return await ViewAsync(p, ct);
    }
    private async Task<WorkDayPlanView> ViewAsync(WorkDayPlan p, CancellationToken ct)
    {
        var staffingSummary = p.PurchaseRequest is { } order ? PurchaseStaffingService.Summary(order) : null;
        var closed = p.PurchaseRequest?.StatusId is S.Declined or S.Cancelled;
        var assigned = p.Assignments.Count(a => a.CancelledAt is null);
        var state = closed ? "CLOSED" : p.Approved is null ? "PENDING_MANAGER" : p.Approved == false ? "DECLINED"
            : p.PurchaseRequest?.StatusId == S.Accepted || assigned > 0 ? "ASSIGNED" : staffingSummary?.InvitedEmployees > 0 ? "AWAITING_STAFF" : "READY_TO_ASSIGN";
        return new(p.Id, p.WorkDate, p.RaftId, p.Raft.Code, p.Raft.ZoneId, p.Raft.Zone.Name, p.JobTypeId, p.JobType.Name,
            p.ResponsibleAccountId, p.ResponsibleAccount.DisplayName, p.CreatorAccount.DisplayName, state, p.ResponseReason,
            !closed && p.WorkDate >= Today && p.Approved is null && p.ResponsibleAccountId == user.AccountId && await access.CanViewAsync(p, ct),
            !closed && p.WorkDate >= Today && p.PurchaseRequest?.StatusId != S.Accepted && await access.CanAssignAsync(p, ct),
            p.PurchaseRequestId, p.PurchaseRequest?.Code, staffingSummary?.Cages, staffingSummary?.RequiredEmployees,
            staffingSummary?.InvitedEmployees ?? 0, staffingSummary?.ApprovedEmployees ?? 0, assigned, p.Note, RowVersionGuard.Encode(p.RowVersion));
    }
}
