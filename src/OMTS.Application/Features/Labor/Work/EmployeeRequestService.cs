using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>
/// LABOR-10 and LABOR-05: an employee sends a request to the Cooperative Owner (leave, or a missed check-in or check-out) and can cancel it
/// while it is Awaiting review; the Owner approves it, with an optional note, or rejects it, with a note (BR-LABOR-08). Approving a missed
/// time records it in the attendance of the day with the method Request. The employee of a call is the one linked to the caller's account.
/// The push to the employee of the result (MSG-LABOR-15) needs the employee app and its linked device (LABOR-06), which does not exist yet:
/// the employee sees the result in their requests.
/// </summary>
public sealed class EmployeeRequestService(
    IEmployeeRequestRepository requests,
    IEmployeeRepository employees,
    IWorkEmployeeReader employeeReader,
    IWorkAssignmentRepository assignments,
    IAttendanceRepository attendances,
    IRaftRepository rafts,
    IPayrollRepository payrolls,
    IAccountRepository accounts,
    INotificationDispatcher notifications,
    ZoneAccessGuard zones,
    RowVersionGuard rowVersions,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const int MaxLeaveDays = 366;

    /// <summary>LABOR-10.0: a new request of the employee of the caller, Awaiting review; the Owner is notified. A retry with the same <c>clientOperationId</c> returns the one saved.</summary>
    public async Task<Result<EmployeeRequestView>> SubmitAsync(SubmitEmployeeRequest request, CancellationToken cancellationToken)
    {
        var employee = await OwnEmployeeAsync(cancellationToken);
        if (employee is null)
        {
            return AppError.Forbidden;
        }

        if (employee.StatusId != StatusIds.Employee.Working)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Employee", statusCatalog.DisplayNameOf(employee.StatusId)));
        }

        var type = request.Type ?? EmployeeRequestType.Leave;
        var today = timeProvider.Today();
        var check = new FieldCheck();
        var reason = check.Required(request.Reason, "Reason", WorkRules.MaxRequestReason);
        check.Check(request.ClientOperationId != Guid.Empty, "Client operation");
        check.Check(Enum.IsDefined(type), "Type");
        DateOnly from, to;
        TimeOnly? time = null;
        if (type == EmployeeRequestType.Leave)
        {
            (from, to) = (request.From ?? default, request.To ?? default);
            check.Check(request.From is not null && request.To is not null && to >= from && to.DayNumber - from.DayNumber < MaxLeaveDays, "Days");
        }
        else
        {
            // A missed check-in or check-out is for one day, today or earlier, and the requested time is not in the future.
            from = to = request.Day ?? request.From ?? default;
            time = WorkRules.ParseTime(request.RequestedTime);
            check.Check((request.Day ?? request.From) is not null && from <= today, "Day (today or earlier)");
            check.Check(time is not null && (from < today || WorkRules.At(from, time.Value) <= timeProvider.GetUtcNow()), "Requested time");
        }

        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        var existing = request.ClientOperationId is { } operationId
            ? await requests.FindByClientOperationAsync(employee.Id, operationId, cancellationToken)
            : null;
        if (existing is not null)
        {
            return EmployeeRequestViews.Of(existing, statusCatalog);
        }

        if (await FinalizedDayAsync(from, to, cancellationToken) is { } finalized)
        {
            return AppError.Conflict(WorkMessages.PayFinalized(finalized), new Dictionary<string, object?> { ["date"] = finalized });
        }

        if (await requests.FindAwaitingAsync(employee.Id, type, from, to, cancellationToken) is { } sent)
        {
            return AppError.AlreadyExists("Employee request", sent.Id);
        }

        var saved = new EmployeeRequest
        {
            EmployeeId = employee.Id, Type = type, FromDate = from, ToDate = to, RequestedTime = time, Reason = reason,
            SentAt = timeProvider.GetUtcNow(), ClientOperationId = request.ClientOperationId,
        };
        requests.Add(saved);
        audit.Record(new AuditEntry("EmployeeRequest.Submitted", nameof(EmployeeRequest))
        {
            RecordId = saved.Id, RecordCode = employee.Code, After = new { employee.FullName, type, from, to, time, reason },
        });
        var text = WorkMessages.RequestText(type, from, to, time);
        await notifications.NotifyAsync(
            await accounts.ListActiveOwnerIdsAsync(cancellationToken),
            new NotificationContent(
                NotificationTypes.EmployeeRequestNew, "New employee request", WorkMessages.RequestNotice(employee.FullName, text, reason),
                new RelatedRecord(nameof(EmployeeRequest), saved.Id)),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        saved.Employee = employee;
        var sentMessage = WorkMessages.RequestSent("Your request");
        return EmployeeRequestViews.Of(saved, statusCatalog) with { Message = sentMessage.Text, MessageCode = sentMessage.Code };
    }

    /// <summary>LABOR-10.2: the employee cancels their own request while it is Awaiting review; it stays in the list as Cancelled. Ask MSG-LABOR-24 first.</summary>
    public async Task<Result<EmployeeRequestView>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var employee = await OwnEmployeeAsync(cancellationToken);
        var request = await requests.GetAsync(id, cancellationToken);
        if (employee is null || request is null || request.EmployeeId != employee.Id)
        {
            return AppError.NotFound;
        }

        if (request.StatusId == StatusIds.EmployeeRequest.Cancelled)
        {
            return EmployeeRequestViews.Of(request, statusCatalog);
        }

        if (request.StatusId != StatusIds.EmployeeRequest.AwaitingReview)
        {
            return AlreadyReviewed(request);
        }

        request.StatusId = StatusIds.EmployeeRequest.Cancelled;
        request.CancelledAt = timeProvider.GetUtcNow();
        audit.RecordStatusChange(nameof(EmployeeRequest), request.Id, employee.Code, StatusIds.EmployeeRequest.AwaitingReview, StatusIds.EmployeeRequest.Cancelled);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // The Owner reviewed it between the read and the save: answer with its result (LABOR-10.2.E1).
            unitOfWork.DiscardChanges();
            var current = await requests.GetAsync(id, cancellationToken);
            return current is { StatusId: not StatusIds.EmployeeRequest.AwaitingReview } && current.StatusId != StatusIds.EmployeeRequest.Cancelled
                ? AlreadyReviewed(current)
                : AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
        }

        return EmployeeRequestViews.Of(request, statusCatalog);
    }

    /// <summary>LABOR-05.0, LABOR-05.2: approves or rejects a request Awaiting review. Cooperative Owner only.</summary>
    public async Task<Result<EmployeeRequestView>> ReviewAsync(Guid id, ReviewEmployeeRequest review, CancellationToken cancellationToken)
    {
        if (!await zones.IsCooperativeOwnerAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var request = await requests.GetAsync(id, cancellationToken);
        if (request is null)
        {
            return AppError.NotFound;
        }

        if (request.StatusId != StatusIds.EmployeeRequest.AwaitingReview)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Employee request", statusCatalog.DisplayNameOf(request.StatusId)));
        }

        var check = new FieldCheck();
        check.Check(review.Approved is not null, "Approved");
        var note = check.Optional(review.Response, "Note", WorkRules.MaxRequestReason);
        check.Check(review.Approved != false || note is not null, "Note");
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(request, review.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var account = currentUser.AccountId ?? throw new InvalidOperationException("A request is reviewed by a logged-in user.");
        var before = request.StatusId;
        if (review.Approved == true && await ApplyAsync(request, review, account, cancellationToken) is { } cannot)
        {
            return cannot;
        }

        request.StatusId = review.Approved == true ? StatusIds.EmployeeRequest.Approved : StatusIds.EmployeeRequest.Rejected;
        request.ReviewNote = note;
        request.ReviewedAt = timeProvider.GetUtcNow();
        request.ReviewedByAccountId = account;
        audit.RecordStatusChange(nameof(EmployeeRequest), request.Id, request.Employee.Code, before, request.StatusId, note);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return EmployeeRequestViews.Of(request, statusCatalog);
    }

    /// <summary>
    /// LABOR-05 steps 5 and 6: nothing of the request is applied while the payroll of the pay period of a day is Awaiting finalization or
    /// Finalized, or when the times would be out of order (MSG-LABOR-16). A missed time replaces the one in the attendance of the day, with
    /// the method Request; a seasonal worker with no work that day gets the job type and raft the Owner chooses (LABOR-05.3). Leave is only recorded.
    /// </summary>
    private async Task<AppError?> ApplyAsync(EmployeeRequest request, ReviewEmployeeRequest review, Guid account, CancellationToken cancellationToken)
    {
        if (await LockedPayrollAsync(request.FromDate, request.ToDate, cancellationToken) is { } payroll)
        {
            return Cannot($"the payroll of {PayPeriod.NameOf(payroll.PeriodEnd)} is {statusCatalog.DisplayNameOf(payroll.StatusId)}");
        }

        if (request.Type == EmployeeRequestType.Leave)
        {
            return null;
        }

        var day = request.FromDate;
        var isCheckIn = request.Type == EmployeeRequestType.MissedCheckIn;
        var time = WorkRules.At(day, request.RequestedTime!.Value);
        var record = await attendances.GetAsync(request.EmployeeId, day, cancellationToken);
        var periods = await assignments.ListOfEmployeesAsync([request.EmployeeId], day, cancellationToken);
        if (isCheckIn ? record?.CheckOutAt <= time : record?.CheckInAt >= time)
        {
            return Cannot(isCheckIn ? "the check-in would not be before the check-out" : "the check-out would not be after the check-in");
        }

        if (periods.Any(p => p.StartsAt is { } start && (isCheckIn ? start <= time : start >= time)))
        {
            return Cannot("a job period of the day would be outside the check-in and the check-out");
        }

        var employee = (await employeeReader.GetForWorkAsync([request.EmployeeId], cancellationToken))[0];
        if (isCheckIn && periods.Count == 0 && WorkRules.TypeOn(employee, day) == EmployeeType.SeasonalWorker
            && await AddAssignmentAsync(employee, day, review, account, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (record is null)
        {
            record = new Attendance { EmployeeId = request.EmployeeId, Date = day };
            attendances.Add(record);
        }

        if (isCheckIn)
        {
            (record.CheckInAt, record.CheckInMethod, record.CheckInRecordedByAccountId, record.CheckInReason) = (time, AttendanceMethod.Request, account, request.Reason);
        }
        else
        {
            (record.CheckOutAt, record.CheckOutMethod, record.CheckOutRecordedByAccountId, record.CheckOutReason) = (time, AttendanceMethod.Request, account, request.Reason);
        }

        audit.Record(new AuditEntry("Attendance.AppliedFromRequest", nameof(Attendance))
        {
            RecordId = record.Id, RecordCode = employee.Code, After = new { record.Date, record.CheckInAt, record.CheckOutAt }, Reason = request.Reason,
        });
        return null;
    }

    /// <summary>LABOR-05.3: the work assignment of the day for a seasonal worker who had none, with the job type and raft the Owner chose.</summary>
    private async Task<AppError?> AddAssignmentAsync(Employee employee, DateOnly day, ReviewEmployeeRequest review, Guid account, CancellationToken cancellationToken)
    {
        var jobType = review.JobTypeId is { } j ? (await assignments.GetJobTypesAsync([j], cancellationToken)).FirstOrDefault() : null;
        if (jobType is null || jobType.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Invalid("Job type");
        }

        var raft = review.RaftId is { } r ? (await rafts.GetByIdsAsync([r], cancellationToken)).FirstOrDefault() : null;
        if (raft is null || raft.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Invalid("Raft");
        }

        assignments.Add(new WorkAssignment { EmployeeId = employee.Id, Date = day, RaftId = raft.Id, JobTypeId = jobType.Id, AssignedByAccountId = account });
        return null;
    }

    /// <summary>A payroll Awaiting finalization or Finalized whose period holds a day of the range, if any; the open payrolls after it are touched, so a send of one and this approval meet (see <see cref="PayPeriodLock"/>).</summary>
    private async Task<Payroll?> LockedPayrollAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var affected = await payrolls.ListEndingOnOrAfterAsync(from, cancellationToken);
        if (affected.FirstOrDefault(p => p.IsLocked && p.PeriodStart <= to) is { } locked)
        {
            return locked;
        }

        foreach (var open in affected)
        {
            unitOfWork.MarkChanged(open);
        }

        return null;
    }

    /// <summary>The first day of the range that a Finalized payroll holds (BR-LABOR-08), or null.</summary>
    private async Task<DateOnly?> FinalizedDayAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var finalized = (await payrolls.ListEndingOnOrAfterAsync(from, cancellationToken))
            .Where(p => p.StatusId == StatusIds.Payroll.Finalized && p.PeriodStart <= to)
            .OrderBy(p => p.PeriodStart)
            .FirstOrDefault();
        return finalized is null ? null : from > finalized.PeriodStart ? from : finalized.PeriodStart;
    }

    private async Task<Employee?> OwnEmployeeAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId ? await employees.GetByAccountIdAsync(accountId, cancellationToken) : null;

    private AppError AlreadyReviewed(EmployeeRequest request) => AppError.Conflict(
        WorkMessages.AlreadyReviewed(request.StatusId == StatusIds.EmployeeRequest.Approved ? "approved" : "rejected"),
        new Dictionary<string, object?> { ["existingId"] = request.Id });

    private static AppError Cannot(string cause) => AppError.Conflict(WorkMessages.RequestCannotBeApproved(cause));
}
