using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// LAB-06 – the Accountant sets each person's pay, also for several people at once. No Owner approval: a saved rate is
/// stored as Approved and applies from its start date. Rate changes never recalculate finalized payslips.
/// </summary>
public sealed class PayRateService(
    IPayRateRepository rates,
    PayRateDrafts drafts,
    PayRateViews views,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string ObjectName = "Pay rate";

    public async Task<Result<PayRateSaved>> CreateAsync(SavePayRateRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(new PayPersonRef(request.EmployeeId, request.WorkerId), request.Body, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        var saved = Add(prepared.Value!);
        return await SaveAsync(saved, cancellationToken) ?? Result<PayRateSaved>.Success(await SavedAsync(saved, cancellationToken));
    }

    /// <summary>All or nothing: every person is checked first; any failure saves nothing and lists every failure.</summary>
    public async Task<Result<IReadOnlyList<PayRateSaved>>> BulkAsync(BulkPayRateRequest request, CancellationToken cancellationToken)
    {
        var people = request.People ?? [];
        if (people.Count == 0 || people.Select(p => p.EmployeeId ?? p.WorkerId).Distinct().Count() != people.Count)
        {
            return AppError.Invalid("People");
        }

        var prepared = new List<PayRateDraft>();
        var failures = new List<BulkFailure>();
        AppError? firstError = null;
        for (var i = 0; i < people.Count; i++)
        {
            var one = await PrepareAsync(people[i], request.Rate, cancellationToken);
            if (one.IsSuccess)
            {
                prepared.Add(one.Value!);
                continue;
            }

            firstError ??= one.Error;

            var field = one.Error!.Details?.GetValueOrDefault("field") as string;
            failures.Add(new BulkFailure(i, people[i].EmployeeId ?? people[i].WorkerId ?? Guid.Empty, null, one.Error.Message.Code, one.Error.Message.Text, field));
        }

        if (failures.Count > 0)
        {
            var first = failures[0];
            return new AppError(firstError!.Kind,new UserMessage(first.Code, first.Message), new Dictionary<string, object?> { ["failures"] = failures, ["field"] = first.Field });
        }

        var saved = prepared.Select(Add).ToList();
        if (await SaveAsync(saved[0], cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        var results = new List<PayRateSaved>();
        foreach (var one in saved)
        {
            results.Add(await SavedAsync(one, cancellationToken));
        }

        return results;
    }

    /// <summary>Edits an unused version (reason required); a version used by a finalized payslip is refused (MSG11).</summary>
    public async Task<Result<PayRateSaved>> UpdateAsync(Guid id, SavePayRateRequest request, CancellationToken cancellationToken)
    {
        var version = await rates.GetAsync(id, cancellationToken);
        if (version is null)
        {
            return AppError.NotFound;
        }

        if ((request.EmployeeId is not null || request.WorkerId is not null) && (request.EmployeeId != version.EmployeeId || request.WorkerId != version.WorkerId))
        {
            return AppError.Invalid("Person");
        }

        var used = (await rates.GetFinalizedPeriodsUsingAsync([version.Id], cancellationToken)).GetValueOrDefault(version.Id) ?? [];
        if (used.Count > 0)
        {
            return PayRateDrafts.UsedByFinalized(used);
        }

        var reasonInvalid = new List<string>();
        var reason = LaborInputRules.Required(request.Reason, LaborInputRules.ReasonLength, "Reason", reasonInvalid);
        if (reason is null)
        {
            return AppError.InvalidFields(reasonInvalid);
        }

        if (await rowVersions.CheckAsync(version, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var input = PayRateInput.From(request.Body, version.WorkerId is not null);
        if (!input.IsSuccess)
        {
            return input.Error!;
        }

        // New dates must stay out of finalized months (dates the edit leaves unchanged are not checked again).
        List<DateOnly> newDates = [];
        if (input.Value!.ValidFrom != version.ValidFrom)
        {
            newDates.Add(input.Value.ValidFrom);
        }

        if (input.Value.ValidTo is { } newEnd && newEnd != version.ValidTo)
        {
            newDates.Add(newEnd);
        }

        if (newDates.Count > 0 && await drafts.CheckDatesOpenAsync(newDates, cancellationToken) is { } locked)
        {
            return locked;
        }

        var others = await rates.ListForPersonAsync(version.EmployeeId, version.WorkerId, cancellationToken);
        var placement = PayRateTimeline.Place(PayRateDrafts.Spans(others), input.Value!.ValidFrom, input.Value.ValidTo, version.Id, closeOpenEnded: false);
        if (placement.OverlapsId is { } overlapId)
        {
            return AppError.AlreadyExists(ObjectName, overlapId);
        }

        var before = PayRateViews.Snapshot(version);
        Apply(version, input.Value);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, PayRateViews.Snapshot(version));
        audit.Record(new AuditEntry("PayRuleVersion.Updated", nameof(PayRuleVersion)) { RecordId = version.Id, Reason = reason, Before = changedBefore, After = changedAfter });
        var saved = new PayRateDraft(version, null, input.Value);
        return await SaveAsync(saved, cancellationToken) ?? Result<PayRateSaved>.Success(await SavedAsync(saved, cancellationToken));
    }

    private Task<Result<PayRateDraft>> PrepareAsync(PayPersonRef person, PayRateBody? body, CancellationToken cancellationToken) =>
        drafts.PrepareAsync(person, body, cancellationToken);

    private PayRateDraft Add(PayRateDraft prepared)
    {
        var (version, closes, input) = prepared;
        if (closes is not null)
        {
            var closedOn = input.ValidFrom.AddDays(-1);
            audit.Record(new AuditEntry("PayRuleVersion.Closed", nameof(PayRuleVersion))
            {
                RecordId = closes.Id, Before = new { ValidTo = closes.ValidTo }, After = new { ValidTo = (DateOnly?)closedOn },
                Reason = $"Closed by the version valid from {input.ValidFrom:yyyy-MM-dd}",
            });
            closes.ValidTo = closedOn;
        }

        Apply(version, input);
        version.EnteredByAccountId = currentUser.AccountId ?? throw new InvalidOperationException("Pay rates need an authenticated caller.");
        (version.StatusId, version.ApprovedByAccountId, version.ApprovedAt) = (StatusIds.PayRule.Approved, version.EnteredByAccountId, timeProvider.GetUtcNow());
        rates.Add(version);
        audit.Record(new AuditEntry("PayRuleVersion.Created", nameof(PayRuleVersion)) { RecordId = version.Id, After = PayRateViews.Snapshot(version) });
        return prepared;
    }

    private static void Apply(PayRuleVersion v, PayRateInput input)
    {
        (v.SalaryType, v.Amount, v.StandardWorkingDays, v.ValidFrom, v.ValidTo) = (input.SalaryType, input.Amount, input.StandardWorkingDays, input.ValidFrom, input.ValidTo);
        v.Components = [.. input.Components];
    }

    // Two versions of a person starting the same day were saved in parallel: the unique index keeps one.
    private async Task<AppError?> SaveAsync(PayRateDraft prepared, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            var existing = await rates.ListForPersonAsync(prepared.Version.EmployeeId, prepared.Version.WorkerId, cancellationToken);
            return AppError.AlreadyExists(ObjectName, existing.FirstOrDefault(v => v.Id != prepared.Version.Id)?.Id ?? prepared.Version.Id);
        }
    }

    private async Task<PayRateSaved> SavedAsync(PayRateDraft prepared, CancellationToken cancellationToken)
    {
        var version = (await rates.GetAsync(prepared.Version.Id, cancellationToken))!;
        var closed = prepared.Closes is null ? null : await views.ViewAsync(prepared.Closes, cancellationToken);
        return new PayRateSaved(await views.ViewAsync(version, cancellationToken), closed);
    }
}
