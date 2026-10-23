using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// LAB-09 – salary advances during the month to an employee or a seasonal worker. An advance stays outstanding until a
/// finalized payroll deducts it (LAB-07/08, oldest first, capped so net pay stays ≥ 0).
/// </summary>
public sealed class SalaryAdvanceService(
    ILaborPaymentRepository payments,
    IWorkforceRepository workforce,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const int MaxPageSize = 100;

    public async Task<Result<AdvanceView>> RecordAsync(RecordAdvanceRequest request, CancellationToken cancellationToken)
    {
        if ((request.EmployeeId is null) == (request.WorkerId is null))
        {
            return AppError.Invalid("Person");
        }

        string? name = null;
        if (request.EmployeeId is { } employeeId)
        {
            var employee = (await workforce.GetEmployeesAsync([employeeId], cancellationToken)).FirstOrDefault();
            if (employee is not { StatusId: StatusIds.Employee.Working })
            {
                return AppError.Invalid("Employee");
            }

            name = employee.FullName;
        }
        else
        {
            var worker = (await workforce.GetWorkersAsync([request.WorkerId!.Value], cancellationToken)).FirstOrDefault();
            if (worker is not { StatusId: StatusIds.Usage.InUse })
            {
                return AppError.Invalid("Worker");
            }

            name = worker.FullName;
        }

        var fieldError = WagePaymentRules.CheckFields(
            request.Amount, request.PaymentDate, request.Method is not null, request.ReferenceCode, request.Recipient, request.Notes,
            timeProvider.Today(), "Amount");
        if (fieldError is not null)
        {
            return fieldError;
        }

        var advance = new LaborPayment
        {
            IsAdvance = true,
            EmployeeId = request.EmployeeId,
            WorkerId = request.WorkerId,
            Recipient = string.IsNullOrWhiteSpace(request.Recipient) ? name : request.Recipient.Trim(),
            AmountPaid = request.Amount!.Value,
            PaymentDate = request.PaymentDate!.Value,
            Method = request.Method!.Value,
            ReferenceCode = string.IsNullOrWhiteSpace(request.ReferenceCode) ? null : request.ReferenceCode.Trim(),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        };
        payments.Add(advance);
        audit.Record(new AuditEntry("SalaryAdvance.Created", nameof(LaborPayment))
        {
            RecordId = advance.Id,
            After = new { advance.EmployeeId, advance.WorkerId, advance.Recipient, advance.AmountPaid, advance.PaymentDate, Method = advance.Method.ToString() },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var own = await ViewsAsync(await payments.ListAdvancesOfAsync(
            request.EmployeeId is { } e ? [e] : [], request.WorkerId is { } w ? [w] : [], cancellationToken), cancellationToken);
        return own.First(a => a.Id == advance.Id);
    }

    /// <summary>
    /// A page of advances (reversals not listed), oldest first; <paramref name="outstandingOnly"/> keeps those with an amount not
    /// yet deducted. Without a person the page is read from the database; the outstanding filter depends on what payrolls
    /// deducted, so that combination still reads every advance and pages the result.
    /// </summary>
    public async Task<Paged<AdvanceView>> ListAsync(
        bool outstandingOnly, Guid? employeeId, Guid? workerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        if (employeeId is null && workerId is null && !outstandingOnly)
        {
            var (items, total) = await payments.PageAdvancesAsync(page, pageSize, cancellationToken);
            // The remaining amount of an advance depends on the person's earlier ones: view all advances of the page's people.
            var related = await payments.ListAdvancesOfAsync(
                items.Where(i => i.EmployeeId is not null).Select(i => i.EmployeeId!.Value).Distinct().ToList(),
                items.Where(i => i.WorkerId is not null).Select(i => i.WorkerId!.Value).Distinct().ToList(), cancellationToken);
            var byId = (await ViewsAsync(related, cancellationToken)).ToDictionary(v => v.Id);
            return new Paged<AdvanceView>(items.Select(i => byId[i.Id]).ToList(), page, pageSize, total);
        }

        var records = employeeId is null && workerId is null
            ? await payments.ListAllAdvancesAsync(cancellationToken)
            : await payments.ListAdvancesOfAsync(employeeId is { } e ? [e] : [], workerId is { } w ? [w] : [], cancellationToken);
        var views = await ViewsAsync(records, cancellationToken);
        var filtered = outstandingOnly ? views.Where(v => v.Remaining > 0).ToList() : views.ToList();
        return new Paged<AdvanceView>(filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, filtered.Count);
    }

    /// <summary>Views of advance records with their remaining amounts.</summary>
    public async Task<IReadOnlyList<AdvanceView>> ViewsAsync(IReadOnlyList<LaborPayment> records, CancellationToken cancellationToken)
    {
        var employeeIds = records.Where(r => r.EmployeeId is not null).Select(r => r.EmployeeId!.Value).Distinct().ToList();
        var workerIds = records.Where(r => r.WorkerId is not null).Select(r => r.WorkerId!.Value).Distinct().ToList();
        var deducted = await payments.GetDeductedAdvanceTotalsAsync(employeeIds, workerIds, cancellationToken);
        var remaining = new Dictionary<Guid, decimal>();
        foreach (var person in employeeIds.Select(id => new PayeeKey(PayeeKind.Employee, id)).Concat(workerIds.Select(id => new PayeeKey(PayeeKind.Worker, id))))
        {
            foreach (var (id, left) in SalaryAdvances.Remaining(records, person, SalaryAdvances.Deducted(deducted, person)))
            {
                remaining[id] = left;
            }
        }

        var reversed = records.Where(r => r.OriginalPaymentId is not null).Select(r => r.OriginalPaymentId!.Value).ToHashSet();
        return records.Where(r => r.OriginalPaymentId is null)
            .Select(r => new AdvanceView(
                r.Id,
                r.EmployeeId is { } e ? new PayeeRef("Employee", e, r.Employee?.FullName ?? string.Empty) : new PayeeRef("Worker", r.WorkerId!.Value, r.Worker?.FullName ?? string.Empty),
                r.AmountPaid, remaining.GetValueOrDefault(r.Id), r.PaymentDate, r.Method, r.Recipient, r.ReferenceCode, r.Notes, reversed.Contains(r.Id),
                r.DeductedInSettlementId, r.DeductedInSettlement?.Payroll?.Period))
            .ToList();
    }
}
