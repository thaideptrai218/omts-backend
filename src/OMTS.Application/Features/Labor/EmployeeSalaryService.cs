using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor;

/// <summary>
/// The salary setup of an employee (tab "Thiết lập lương", owner decision of 27/11/2026): a fixed monthly amount or pay by the hour,
/// each from an effective date, like the hourly rate of a job type (<see cref="PayRateService"/>). The date of an existing setup
/// replaces that setup and the others stay, so a change never rewrites another month. A date inside a finalized payroll is refused
/// (BR-LABOR-13). Each save moves the row version of the employee, so parallel saves answer MSG06.
/// </summary>
public sealed class EmployeeSalaryService(
    IEmployeeSalaryRepository salaries,
    PayPeriodLock payPeriods,
    RowVersionGuard rowVersions,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Not an SRS limit: a month above it can only be a typing error, and a payroll total must still fit decimal(18,0).</summary>
    public const decimal MaxMonthlyAmount = 9_999_999_999m;

    public async Task<Result<EmployeeSalaryView>> GetAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await salaries.GetWithSalarySetupsAsync(employeeId, cancellationToken) is { } employee ? View(employee) : AppError.NotFound;

    public async Task<Result<EmployeeSalaryView>> ChangeAsync(Guid employeeId, ChangeSalaryRequest request, CancellationToken cancellationToken)
    {
        if (await salaries.GetWithSalarySetupsAsync(employeeId, cancellationToken) is not { } employee)
        {
            return AppError.NotFound;
        }

        var check = new FieldCheck();
        var (type, amount) = Fields(check, request.Type, request.MonthlyAmount);
        var from = JobTypeFields.EffectiveDate(check, request.EffectiveFrom);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        // A Left employee keeps the setups that paid their months; a new one would pay nobody.
        if (employee.StatusId != StatusIds.Employee.Working)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Employee", statusCatalog.DisplayNameOf(employee.StatusId)));
        }

        if (await rowVersions.CheckAsync(employee, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var sameDate = employee.SalarySetups.FirstOrDefault(s => s.EffectiveFrom == from);
        if (sameDate is not null && sameDate.Type == type && sameDate.MonthlyAmount == amount)
        {
            // Nothing to save. The employee and its setups came from one read, so the version is read again before it is handed back (NFR-09).
            await unitOfWork.ReloadAsync(employee, cancellationToken);
            return await rowVersions.CheckAsync(employee, request.RowVersion, cancellationToken) is { } moved ? moved : View(employee);
        }

        if (await payPeriods.CheckAsync(from, cancellationToken) is { } locked)
        {
            return locked;
        }

        audit.Record(new AuditEntry("Employee.SalaryChanged", nameof(Employee))
        {
            RecordId = employee.Id,
            RecordCode = employee.Code,
            Before = sameDate is null ? null : new { sameDate.Type, sameDate.MonthlyAmount, EffectiveFrom = from },
            After = new { Type = type, MonthlyAmount = amount, EffectiveFrom = from },
        });
        var enteredBy = currentUser.AccountId ?? throw new InvalidOperationException("A salary is entered by a logged-in user.");
        if (sameDate is null)
        {
            // The employee is tracked, so adding the row also puts it in employee.SalarySetups for the answer.
            salaries.Add(new EmployeeSalarySetup { EmployeeId = employee.Id, Type = type, MonthlyAmount = amount, EffectiveFrom = from, EnteredByAccountId = enteredBy });
        }
        else
        {
            sameDate.Type = type;
            sameDate.MonthlyAmount = amount;
            sameDate.EnteredByAccountId = enteredBy;
        }

        unitOfWork.MarkChanged(employee);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return View(employee);
        }
        catch (DuplicateRecordException)
        {
            // Parallel saves of one date: the insert meets its index before the version check, which would answer the same.
            return AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
        }
    }

    /// <summary>A defined type; the amount is required and whole for a fixed salary, and must be absent for hourly pay.</summary>
    internal static (SalaryType Type, decimal? Amount) Fields(FieldCheck check, SalaryType? type, decimal? amount)
    {
        if (type is not { } t || !Enum.IsDefined(t))
        {
            check.Check(false, "Salary type");
            return (default, null);
        }

        if (t == SalaryType.Hourly)
        {
            check.Check(amount is null, "Monthly amount");
            return (t, null);
        }

        var whole = amount is { } value && value == decimal.Truncate(value) && value is > 0 and <= MaxMonthlyAmount;
        check.Check(whole, "Monthly amount");
        return (t, whole ? decimal.Truncate(amount!.Value) : null);
    }

    private EmployeeSalaryView View(Employee employee)
    {
        var today = timeProvider.Today();
        var current = EmployeeSalarySetup.EffectiveOn(employee.SalarySetups, today);
        var employeeType = EmployeeTypePeriod.TypeOn(employee.TypePeriods, today);
        return new EmployeeSalaryView(
            employee.Id,
            employeeType,
            current is null ? null : ViewOf(current),
            current?.Type ?? EmployeeSalarySetup.DefaultFor(employeeType),
            employee.SalarySetups.OrderByDescending(s => s.EffectiveFrom).Select(ViewOf).ToList(),
            RowVersionGuard.Encode(employee.RowVersion));
    }

    private static SalarySetupView ViewOf(EmployeeSalarySetup setup) => new(setup.Type, setup.MonthlyAmount, setup.EffectiveFrom, setup.CreatedAt);
}
