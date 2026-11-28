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
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor;

/// <summary>
/// A new shift rate of a job type from its date, with the rules of the hourly rate (<see cref="PayRateService"/>): the date of an existing rate replaces that rate, the others stay, a date
/// inside a finalized payroll is MSG-LABOR-04, and each save moves the row version of the job type so parallel saves answer MSG06.
/// </summary>
public sealed class ShiftRateService(
    IJobTypeRepository jobTypes,
    JobTypeQueryService queries,
    PayPeriodLock payPeriods,
    RowVersionGuard rowVersions,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<JobTypeSaved>> ChangeShiftRateAsync(Guid id, ChangeShiftRateRequest request, CancellationToken cancellationToken)
    {
        if (await jobTypes.GetForEditAsync(id, cancellationToken) is not { } jobType)
        {
            return AppError.NotFound;
        }

        var check = new FieldCheck();
        var amount = JobTypeFields.ShiftRate(check, request.ShiftRate);
        var from = JobTypeFields.EffectiveDate(check, request.EffectiveFrom);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (jobType.StatusId != StatusIds.Usage.InUse)
        {
            return queries.WrongStatus(jobType);
        }

        if (await rowVersions.CheckAsync(jobType, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (await payPeriods.CheckAsync(from, cancellationToken) is { } locked)
        {
            return locked;
        }

        var sameDate = jobType.ShiftRates.FirstOrDefault(r => r.EffectiveFrom == from);
        if (sameDate?.Amount == amount)
        {
            // Nothing to save, so the version is read again before the answer: a parallel save may have landed inside the read (NFR-09).
            await unitOfWork.ReloadAsync(jobType, cancellationToken);
            return await rowVersions.CheckAsync(jobType, request.RowVersion, cancellationToken) is { } moved ? moved : queries.Saved(jobType);
        }

        audit.Record(new AuditEntry("JobType.ShiftRateChanged", nameof(JobType))
        {
            RecordId = jobType.Id,
            RecordCode = jobType.Name,
            Before = sameDate is null ? null : new { ShiftRate = sameDate.Amount, EffectiveFrom = from },
            After = new { ShiftRate = amount, EffectiveFrom = from },
        });
        var enteredBy = currentUser.AccountId ?? throw new InvalidOperationException("Rates are entered by a logged-in user.");
        if (sameDate is null)
        {
            jobTypes.AddShiftRate(new ShiftRate { JobTypeId = jobType.Id, Amount = amount, EffectiveFrom = from, EnteredByAccountId = enteredBy });
        }
        else
        {
            sameDate.Amount = amount;
            sameDate.EnteredByAccountId = enteredBy;
        }

        unitOfWork.MarkChanged(jobType);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return queries.Saved(jobType);
        }
        catch (DuplicateRecordException)
        {
            // Parallel saves of one date: the insert meets its index before the version check, which would answer the same.
            return AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
        }
    }
}
