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
/// LABOR-11.0: a new hourly rate from its date (BR-LABOR-12). The date of an existing rate replaces that rate, and the others
/// stay, so a change never rewrites another date. Each save moves the row version of the job type, so parallel saves of one
/// job type answer MSG06. The client asks MSG-LABOR-27 before it sends.
/// </summary>
public sealed class PayRateService(
    IJobTypeRepository jobTypes,
    JobTypeQueryService queries,
    PayPeriodLock payPeriods,
    RowVersionGuard rowVersions,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<JobTypeSaved>> ChangeRateAsync(Guid id, ChangeRateRequest request, CancellationToken cancellationToken)
    {
        if (await jobTypes.GetForEditAsync(id, cancellationToken) is not { } jobType)
        {
            return AppError.NotFound;
        }

        var check = new FieldCheck();
        var rate = JobTypeFields.HourlyRate(check, request.HourlyRate);
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

        var sameDate = jobType.Rates.FirstOrDefault(r => r.EffectiveFrom == from);
        if (sameDate?.HourlyRate == rate)
        {
            return queries.Saved(jobType);
        }

        audit.Record(new AuditEntry("JobType.RateChanged", nameof(JobType))
        {
            RecordId = jobType.Id,
            RecordCode = jobType.Name,
            Before = sameDate is null ? null : new { sameDate.HourlyRate, EffectiveFrom = from },
            After = new { HourlyRate = rate, EffectiveFrom = from },
        });
        var enteredBy = currentUser.AccountId ?? throw new InvalidOperationException("Rates are entered by a logged-in user.");
        if (sameDate is null)
        {
            jobTypes.AddRate(new PayRate { JobTypeId = jobType.Id, HourlyRate = rate, EffectiveFrom = from, EnteredByAccountId = enteredBy });
        }
        else
        {
            sameDate.HourlyRate = rate;
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
