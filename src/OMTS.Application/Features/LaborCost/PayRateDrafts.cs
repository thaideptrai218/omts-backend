using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.LaborCost;

/// <summary>A checked new version and the open-ended version it closes, not yet added to the unit of work.</summary>
public sealed record PayRateDraft(PayRuleVersion Version, PayRuleVersion? Closes, PayRateInput Input);

/// <summary>
/// LAB-06 checks of one new version: the person (Employed employee or In Use worker), the rate rules and the timeline
/// (an earlier open-ended version is closed on ValidFrom − 1 day; any other overlap is MSG03). A new or re-dated version never
/// overlaps a finalized month (PERIOD_LOCKED) and never closes one that a finalized payslip used (MSG11).
/// </summary>
public sealed class PayRateDrafts(IPayRateRepository rates, IWorkforceRepository workforce, PayrollPeriodLock periodLock)
{
    public async Task<Result<PayRateDraft>> PrepareAsync(PayPersonRef person, PayRateBody? body, CancellationToken cancellationToken)
    {
        if ((person.EmployeeId is null) == (person.WorkerId is null))
        {
            return AppError.Invalid("Person");
        }

        if (person.EmployeeId is { } employeeId
            && (await workforce.GetEmployeesAsync([employeeId], cancellationToken)).FirstOrDefault() is not { StatusId: StatusIds.Employee.Working })
        {
            return AppError.Invalid("Employee");
        }

        var worker = person.WorkerId is { } workerId ? (await workforce.GetWorkersAsync([workerId], cancellationToken)).FirstOrDefault() : null;
        if (person.WorkerId is not null && worker is not { StatusId: StatusIds.Usage.InUse })
        {
            return AppError.Invalid("Worker");
        }

        var input = PayRateInput.From(body, person.WorkerId is not null);
        if (!input.IsSuccess)
        {
            return input.Error!;
        }

        if (await CheckRangeOpenAsync(input.Value!.ValidFrom, input.Value.ValidTo, cancellationToken) is { } locked)
        {
            return locked;
        }

        var existing = await rates.ListForPersonAsync(person.EmployeeId, person.WorkerId, cancellationToken);
        var placement = PayRateTimeline.Place(Spans(existing), input.Value!.ValidFrom, input.Value.ValidTo, null, closeOpenEnded: true);
        if (placement.OverlapsId is { } overlapId)
        {
            return AppError.AlreadyExists(PayRateService.ObjectName, overlapId);
        }

        var closes = existing.FirstOrDefault(v => v.Id == placement.ClosesId);
        if (closes is not null)
        {
            // Closing on ValidFrom − 1 day must not cut into a finalized month that this version priced.
            var closedOn = input.Value.ValidFrom.AddDays(-1);
            var cut = (await rates.GetFinalizedPeriodsUsingAsync([closes.Id], cancellationToken)).GetValueOrDefault(closes.Id)?
                .Where(key => PayrollPeriod.TryParse(key, out var period) && period.End > closedOn).ToList();
            if (cut is { Count: > 0 })
            {
                return UsedByFinalized(cut);
            }
        }

        var version = new PayRuleVersion { EmployeeId = person.EmployeeId, WorkerId = person.WorkerId };
        return new PayRateDraft(version, closes, input.Value);
    }

    /// <summary>PERIOD_LOCKED when [validFrom, validTo or open end] overlaps any finalized month: a new or re-dated version never covers one.</summary>
    public Task<AppError?> CheckRangeOpenAsync(DateOnly validFrom, DateOnly? validTo, CancellationToken cancellationToken) =>
        periodLock.CheckRangeAsync(validFrom, validTo, cancellationToken);

    /// <summary>MSG11: the version is used by finalized payslips of these periods (keys like 2026-09).</summary>
    public static AppError UsedByFinalized(IReadOnlyCollection<string> periods) =>
        AppError.Conflict(UserMessages.StillInUse(periods.Count, "finalized payslips " + string.Join(", ", periods.Select(PayrollPeriod.LabelOf))));

    public static List<RateSpan> Spans(IEnumerable<PayRuleVersion> versions) =>
        versions.Select(v => new RateSpan(v.Id, v.ValidFrom, v.ValidTo)).ToList();
}
