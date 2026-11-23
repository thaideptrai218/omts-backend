using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// L9 and L10: what ending an employment on a given last working day does and leaves open, for the screen that asks the Owner to confirm: the assignments that are removed, days checked in and not
/// out, advances not yet deducted, payslips still owed and the linked login account. It changes nothing. The same summary comes back with the end action.
/// </summary>
public sealed class EmploymentConsequencesService(
    IEmployeeRepository employees,
    IEmployeeEndingReader ending,
    IWagePaymentRepository payments,
    IStatusCatalog statusCatalog,
    EmployeeCallerAccess callers,
    TimeProvider timeProvider)
{
    /// <param name="lastWorkingDay">Today or earlier; today when not given.</param>
    public async Task<Result<EndingConsequences>> PreviewAsync(Guid employeeId, DateOnly? lastWorkingDay, CancellationToken cancellationToken)
    {
        var employee = await employees.GetForEditAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        var caller = await callers.GetAsync(cancellationToken);
        if (!caller.Sees(employee))
        {
            return AppError.NotFound;
        }

        if (!caller.Maintains(employee))
        {
            return AppError.Forbidden;
        }

        var today = timeProvider.Today();
        var lastDay = lastWorkingDay ?? today;
        return lastDay > today ? AppError.Invalid("Last working day") : await SummaryAsync(employee, lastDay, caller, cancellationToken);
    }

    /// <summary>The summary for an employee the caller may maintain, as of <paramref name="lastDay"/>.</summary>
    public async Task<EndingConsequences> SummaryAsync(Employee employee, DateOnly lastDay, EmployeeCaller caller, CancellationToken cancellationToken)
    {
        var future = await ending.ListAssignmentsAfterAsync(employee.Id, lastDay, cancellationToken);
        var open = await ending.ListOpenCheckInDaysAsync(employee.Id, cancellationToken);
        var advances = (await payments.ListOpenAdvancesAsync([employee.Id], cancellationToken)).Sum(a => a.Remaining);
        var lines = await payments.ListFinalizedLinesAsync(employee.Id, cancellationToken);
        var paid = await payments.PaidAmountsAsync(lines.Select(l => l.Id).ToList(), cancellationToken);
        var owed = lines.Select(l => l.Net - paid.GetValueOrDefault(l.Id)).Where(o => o > 0).ToList();
        var account = caller.IsOwner ? employee.Account : null;
        return new EndingConsequences(
            lastDay, future, open, advances, owed.Count, owed.Sum(),
            account is null ? null : new LinkedAccountView(account.Id, account.Username, account.DisplayName, statusCatalog.DisplayNameOf(account.StatusId)),
            account is { SubjectType: SubjectType.Staff, StatusId: StatusIds.Account.Active });
    }
}
