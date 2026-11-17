using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.EmployeeApp;

/// <summary>
/// LABOR-03 – a Zone Manager of the employee, or the Cooperative Owner, issues the login code the employee enters in the employee app
/// (BR-LABOR-02). The code has 6 digits, works once, expires after 5 minutes and is cancelled by the 5th wrong entry; a new code
/// cancels the earlier one. It is shown to the issuer and sent to the issuer's own account as a notification, never to the employee.
/// Only a keyed hash of it is stored and it is never written to the audit log.
/// </summary>
public sealed class EmployeeLoginCodeService(
    IEmployeeRepository employees,
    IEmployeeAppRepository app,
    EmployeeCallerAccess callers,
    ICurrentUser currentUser,
    IVerificationCodeHasher codeHasher,
    INotificationDispatcher notifications,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    // Two issuers at the same moment meet the unique index of the open code: the second one cancels the first one's code and goes on.
    private const int MaxIssueAttempts = 5;

    public async Task<Result<EmployeeLoginCodeIssued>> IssueAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        if (currentUser.AccountId is not { } issuerId)
        {
            return AppError.Forbidden;
        }

        for (var attempt = 1; ; attempt++)
        {
            var employee = await employees.GetForEditAsync(employeeId, cancellationToken);
            if (employee is null)
            {
                return AppError.NotFound;
            }

            // A Zone Manager issues codes only for the employees they maintain; the Owner for everyone (BR-LABOR-02, BR-LABOR-01).
            if (!(await callers.GetAsync(cancellationToken)).Maintains(employee))
            {
                return AppError.Forbidden;
            }

            if (NotIssuable(employee, timeProvider.Today()) is { } refused)
            {
                return refused;
            }

            try
            {
                return await IssueOnceAsync(employee, issuerId, cancellationToken);
            }
            catch (Exception exception) when (exception is DuplicateRecordException or ConcurrencyConflictException && attempt < MaxIssueAttempts)
            {
                // Another code was issued (or used) for the same employee between the two saves: read the open codes again.
                unitOfWork.DiscardChanges();
            }
            catch (DuplicateRecordException)
            {
                return AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
            }
        }
    }

    /// <summary>LABOR-03.0.E1 (MSG-LABOR-09): an employee who is Left, or has no phone number for the employee app, gets no code.</summary>
    private static AppError? NotIssuable(Employee employee, DateOnly today)
    {
        if (!EmployeeAppAccess.IsEmployedToday(employee, today))
        {
            return Refusal(employee, "the employee has left", "Left");
        }

        return string.IsNullOrEmpty(employee.PhoneNumber)
            ? Refusal(employee, "the profile has no phone number for the employee app", "NoPhone")
            : null;
    }

    private static AppError Refusal(Employee employee, string reason, string reasonCode) =>
        AppError.Conflict(UserMessages.LoginCodeNotIssued(employee.FullName, reason), new Dictionary<string, object?> { ["reason"] = reasonCode });

    private async Task<EmployeeLoginCodeIssued> IssueOnceAsync(Employee employee, Guid issuerId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Step 2: the earlier code is cancelled in a save of its own, before the new one is added. The index of the one open code is
        // checked statement by statement, and the order of the statements of one save is not promised.
        var earlier = await app.ListOpenCodesAsync(employee.Id, cancellationToken);
        if (earlier.Count > 0)
        {
            foreach (var old in earlier)
            {
                old.CancelledAt = now;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var code = SecretHashing.NewSixDigitCode();
        var row = new EmployeeLoginCode { EmployeeId = employee.Id, IssuedByAccountId = issuerId, IssuedAt = now, ExpiresAt = now + EmployeeLoginCode.Lifetime };
        row.CodeHash = codeHasher.Hash(row.Id, code);
        app.Add(row);

        // Step 5: what is recorded is who issued a code for whom and when it expires, never the code (GBR-02).
        audit.Record(new AuditEntry("Employee.LoginCodeIssued", nameof(Employee))
        {
            RecordId = employee.Id,
            After = new { row.ExpiresAt },
        });

        // Step 4: to the issuer's own account only, whatever their preferences keep out of push (BR-ACCOUNT-12, GFR-06).
        var message = UserMessages.LoginCodeIssued(employee.FullName, code, row.ExpiresAt, employee.PhoneNumber!);
        await notifications.NotifyAsync([issuerId], new NotificationContent(NotificationTypes.EmployeeLoginCode, "Employee login code", message), cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new EmployeeLoginCodeIssued(code, row.ExpiresAt, employee.PhoneNumber!, employee.FullName, message.Text, message.Code);
    }
}
