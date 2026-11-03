using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Sales.Requests.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using S = OMTS.Domain.Constants.StatusIds.PurchaseRequest;

namespace OMTS.Application.Features.Sales.Requests;

/// <summary>Yêu cầu 31/10/2026: nhân viên xác nhận ca trước khi Chủ HTX nhận đơn theo lồng.</summary>
public sealed class PurchaseStaffingService(
    IPurchaseRequestRepository requests, IWorkforceRepository workforce, IAccountRepository accounts,
    ICurrentUser user, PermissionService permissions, RowVersionGuard versions, IStatusCatalog statuses,
    AuditLogService audit, INotificationDispatcher notifications, IUnitOfWork uow, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);
    private async Task<bool> IsOwnerAsync(CancellationToken ct) => user.AccountId is { } id &&
        (await permissions.GetAccessAsync(id, ct))?.IsCooperativeOwner == true;

    public async Task<Result<IReadOnlyList<StaffingEmployeeOption>>> EmployeesAsync(DateOnly day, CancellationToken ct)
    {
        if (!await IsOwnerAsync(ct)) return AppError.Forbidden;
        if (day == default || day < Today) return AppError.Invalid("Work date");
        var result = new List<StaffingEmployeeOption>();
        foreach (var e in await requests.StaffingEmployeesAsync(ct))
        {
            var reason = await UnavailableAsync(e, day, null, ct);
            result.Add(new(e.Id, e.FullName, reason is null, reason));
        }
        return Result<IReadOnlyList<StaffingEmployeeOption>>.Success(result);
    }

    public async Task<Result<IReadOnlyList<PurchaseShiftInvitationView>>> InvitationsAsync(Guid id, CancellationToken ct)
    {
        if (!await IsOwnerAsync(ct)) return AppError.Forbidden;
        var row = await requests.GetAsync(id, ct);
        if (row is null) return AppError.NotFound;
        return Result<IReadOnlyList<PurchaseShiftInvitationView>>.Success(row.ShiftInvitations.OrderBy(i => i.CreatedAt)
            .Select(i => new PurchaseShiftInvitationView(i.Id, i.EmployeeId, i.Employee.FullName, i.Approved, i.Reason, i.RespondedAt)).ToList());
    }

    public async Task<Result<PurchaseStaffingView>> PlanAsync(Guid id, PlanPurchaseStaffing input, CancellationToken ct)
    {
        if (!await IsOwnerAsync(ct)) return AppError.Forbidden;
        var row = await requests.GetAsync(id, ct);
        if (row is null) return AppError.NotFound;
        if (row.StatusId is not (S.AwaitingResponse or S.AwaitingStaff)) return AppError.Invalid("Purchase request status");
        if (await versions.CheckAsync(row, input.RowVersion, ct) is { } stale) return stale;
        if (input.Cages <= 0 || input.Cages > row.RequestedCages || input.CagesPerEmployeePerDay <= 0)
            return AppError.Invalid("Staffing capacity");
        if (input.WorkDate == default || input.WorkDate < Today || input.PickupDate < input.WorkDate)
            return AppError.Invalid("Work date");
        if (input.EmployeeIds is null || input.EmployeeIds.Count is < 1 or > 100 ||
            input.EmployeeIds.Distinct().Count() != input.EmployeeIds.Count || input.Note?.Length > 500)
            return AppError.Invalid("Shift invitations");
        // Giữ nguyên nội dung đã được nhân viên đọc; có thể mời thêm người thay vì sửa ca đã gửi.
        if (row.WorkDate is not null && (row.WorkDate != input.WorkDate || row.PlannedCages != input.Cages ||
            row.PlannedPickupDate != input.PickupDate || row.CagesPerEmployeePerDay != input.CagesPerEmployeePerDay ||
            row.StaffingNote != input.Note?.Trim())) return AppError.Invalid("Staffing plan already sent");
        var existingIds = row.ShiftInvitations.Select(i => i.EmployeeId).ToHashSet();
        var newIds = input.EmployeeIds.Where(e => !existingIds.Contains(e)).ToHashSet();
        if (row.ShiftInvitations.Count + newIds.Count > 100) return AppError.Invalid("Shift invitations");
        var employees = (await requests.StaffingEmployeesAsync(ct)).Where(e => newIds.Contains(e.Id)).ToList();
        if (employees.Count != newIds.Count) return AppError.Invalid("Employee");
        foreach (var e in employees)
            if (await UnavailableAsync(e, input.WorkDate, row.Id, ct) is not null) return AppError.Invalid("Employee unavailable");
        row.PlannedCages = input.Cages; row.WorkDate = input.WorkDate; row.PlannedPickupDate = input.PickupDate;
        row.CagesPerEmployeePerDay = input.CagesPerEmployeePerDay; row.StaffingNote = input.Note?.Trim();
        row.StatusId = S.AwaitingStaff;
        foreach (var e in employees)
        {
            var invite = new PurchaseShiftInvitation { PurchaseRequestId = row.Id, PurchaseRequest = row, EmployeeId = e.Id, Employee = e };
            requests.AddInvitation(invite);
            // EF cập nhật navigation; thêm rõ ràng để adapter khác cũng trả đúng tiến độ.
            if (!row.ShiftInvitations.Contains(invite)) row.ShiftInvitations.Add(invite);
            await notifications.NotifyAsync([e.AccountId!.Value], "PURCHASE_SHIFT_INVITED", "Mời xác nhận ca làm",
                $"{row.Code}: {row.PlannedCages} lồng, ngày {row.WorkDate:dd/MM/yyyy}. Vui lòng đồng ý hoặc từ chối ca.",
                nameof(PurchaseRequest), row.Code, false, ct);
        }
        if (employees.Count > 0)
        {
            uow.MarkChanged(row);
            audit.Record(new AuditEntry("PurchaseRequest.StaffInvited", nameof(PurchaseRequest))
            { RecordId = row.Id, RecordCode = row.Code, After = new { row.PlannedCages, row.WorkDate, row.CagesPerEmployeePerDay, EmployeeIds = newIds } });
            await uow.SaveChangesAsync(ct);
        }
        return Summary(row)!;
    }

    public async Task<Result<IReadOnlyList<MyPurchaseShiftView>>> MineAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from == default || to < from || to.DayNumber - from.DayNumber > 62) return AppError.Invalid("Work date");
        var employee = user.AccountId is { } id ? await workforce.MyEmployeeAsync(id, ct) : null;
        if (employee is null) return AppError.Forbidden;
        return Result<IReadOnlyList<MyPurchaseShiftView>>.Success((await requests.EmployeeInvitationsAsync(employee.Id, from, to, ct)).Select(MyView).ToList());
    }

    public async Task<Result<MyPurchaseShiftView>> RespondAsync(Guid id, RespondPurchaseShift input, CancellationToken ct)
    {
        var employee = user.AccountId is { } accountId ? await workforce.MyEmployeeAsync(accountId, ct) : null;
        if (employee is null) return AppError.Forbidden;
        var invite = await requests.InvitationAsync(id, ct);
        if (invite is null) return AppError.NotFound;
        if (invite.EmployeeId != employee.Id) return AppError.Forbidden;
        var row = invite.PurchaseRequest;
        if (row.StatusId != S.AwaitingStaff || row.WorkDate < Today) return AppError.Invalid("Purchase request status");
        // Retry cùng phản hồi không gửi thông báo hoặc chiếm ca lần thứ hai.
        if (invite.Approved is not null)
            return invite.Approved == input.Approved && invite.Reason == input.Reason?.Trim()
                ? MyView(invite) : AppError.AlreadyExists("Shift already answered", invite.Id);
        if (await versions.CheckAsync(invite, input.RowVersion, ct) is { } stale) return stale;
        if (input.Reason?.Length > 500 || (!input.Approved && string.IsNullOrWhiteSpace(input.Reason))) return AppError.Invalid("Response");
        if (input.Approved)
        {
            if (Summary(row)!.HasEnoughStaff) return AppError.Invalid("Staffing already sufficient");
            if (await UnavailableAsync(invite.Employee, row.WorkDate!.Value, row.Id, ct) is not null) return AppError.Invalid("Employee unavailable");
        }
        invite.Approved = input.Approved; invite.Reason = input.Reason?.Trim(); invite.RespondedAt = clock.GetUtcNow();
        invite.ReservedWorkDate = input.Approved ? row.WorkDate : null;
        // Cùng sửa parent để hai phản hồi cuối/nhận đơn/hủy không vượt qua nhau với dữ liệu cũ.
        uow.MarkChanged(row);
        audit.Record(new AuditEntry("PurchaseShift.Responded", nameof(PurchaseShiftInvitation))
        { RecordId = invite.Id, RecordCode = row.Code, After = new { invite.Approved, invite.Reason, row.WorkDate } });
        var summary = Summary(row)!;
        await notifications.NotifyAsync(await accounts.ListActiveOwnerIdsAsync(ct), "PURCHASE_SHIFT_RESPONDED", "Nhân viên phản hồi ca làm",
            $"{row.Code}: {invite.Employee.FullName} {(input.Approved ? "đồng ý" : "từ chối")}. Đã đủ {summary.ApprovedEmployees}/{summary.RequiredEmployees} người. {invite.Reason}",
            nameof(PurchaseRequest), row.Code, false, ct);
        await uow.SaveChangesAsync(ct);
        return MyView(invite);
    }

    public async Task<AppError?> ValidateAcceptanceAsync(PurchaseRequest row, int? cages, DateOnly? pickup, CancellationToken ct)
    {
        var summary = Summary(row);
        if (row.StatusId != S.AwaitingStaff || summary?.HasEnoughStaff != true) return AppError.Invalid("Insufficient approved staff");
        if (row.PlannedCages != cages || row.PlannedPickupDate != pickup || row.WorkDate < Today) return AppError.Invalid("Staffing plan mismatch");
        foreach (var invite in row.ShiftInvitations.Where(i => i.Approved == true && i.ReservedWorkDate == row.WorkDate))
            if (await UnavailableAsync(invite.Employee, row.WorkDate!.Value, row.Id, ct) is not null) return AppError.Invalid("Employee unavailable");
        return null;
    }

    public async Task ReleaseAsync(PurchaseRequest row, CancellationToken ct)
    {
        foreach (var i in row.ShiftInvitations) i.ReservedWorkDate = null;
        var recipients = row.ShiftInvitations.Select(i => i.Employee.AccountId).OfType<Guid>().Distinct().ToList();
        if (recipients.Count > 0)
            await notifications.NotifyAsync(recipients, "PURCHASE_SHIFT_CLOSED", "Ca làm đã đóng",
                $"{row.Code}: đơn đã {(row.StatusId == S.Cancelled ? "hủy" : "bị từ chối")}, không còn giữ ca ngày {row.WorkDate:dd/MM/yyyy}.",
                nameof(PurchaseRequest), row.Code, false, ct);
    }

    private async Task<string?> UnavailableAsync(Employee employee, DateOnly day, Guid? exceptRequestId, CancellationToken ct)
    {
        if (employee.StatusId != StatusIds.Employee.Working || employee.Account is not { StatusId: StatusIds.Account.Active, SubjectType: SubjectType.Staff })
            return "Chưa có tài khoản nhân viên đang hoạt động";
        if (await workforce.HasApprovedLeaveAsync(employee.Id, day, ct)) return "Đã được duyệt nghỉ phép";
        if (await requests.HasShiftReservationAsync(employee.Id, day, exceptRequestId, ct)) return "Đã đồng ý ca của đơn khác";
        if (await workforce.EmployeeAssignmentAsync(employee.Id, day, ct) is not null) return "Đã có phân công trong ngày";
        return null;
    }

    public static PurchaseStaffingView? Summary(PurchaseRequest row)
    {
        if (row.PlannedCages is not { } cages || row.WorkDate is not { } day || row.PlannedPickupDate is not { } pickup || row.CagesPerEmployeePerDay is not > 0) return null;
        var capacity = row.CagesPerEmployeePerDay.Value;
        var required = 1 + (cages - 1) / capacity;
        var approved = row.ShiftInvitations.Count(i => i.Approved == true && i.ReservedWorkDate == day);
        return new(cages, day, pickup, capacity, required, row.ShiftInvitations.Count, approved,
            row.ShiftInvitations.Count(i => i.Approved == false), row.ShiftInvitations.Count(i => i.Approved is null), approved >= required, row.StaffingNote);
    }

    private MyPurchaseShiftView MyView(PurchaseShiftInvitation invite)
    {
        var row = invite.PurchaseRequest; var s = Summary(row)!;
        return new(invite.Id, row.Id, row.Code, row.ProductName, s.Cages, s.WorkDate, s.PickupDate,
            s.RequiredEmployees, s.ApprovedEmployees, invite.Approved, invite.Reason, s.Note, statuses.CodeOf(row.StatusId),
            row.StatusId == S.AwaitingStaff && invite.Approved is null && s.WorkDate >= Today && !s.HasEnoughStaff,
            RowVersionGuard.Encode(invite.RowVersion));
    }
}
