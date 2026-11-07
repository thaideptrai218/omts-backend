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
/// LABOR-11.1, LABOR-11.2, LABOR-11.3: the Accountant or the Cooperative Owner adds, renames, discontinues and resumes job
/// types (the rates: <see cref="PayRateService"/>). The client asks MSG-LABOR-27 or MSG-GEN-04 before it sends.
/// </summary>
public sealed class JobTypeService(
    IJobTypeRepository jobTypes,
    JobTypeQueryService queries,
    PayPeriodLock payPeriods,
    RowVersionGuard rowVersions,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    /// <summary>The audit action of a new job type: its row keeps the name it was added under, whatever the name becomes.</summary>
    public const string CreatedAction = "JobType.Created";

    /// <summary>The job type and its first rate in one save, In Use.</summary>
    public async Task<Result<JobTypeSaved>> AddAsync(AddJobTypeRequest request, CancellationToken cancellationToken)
    {
        var check = new FieldCheck();
        var name = JobTypeFields.Name(check, request.Name);
        var rate = JobTypeFields.HourlyRate(check, request.HourlyRate);
        var from = JobTypeFields.EffectiveDate(check, request.EffectiveFrom);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await jobTypes.FindByNameAsync(name, null, cancellationToken) is { } existing)
        {
            return AppError.AlreadyExists("Job type", existing.Id);
        }

        if (await payPeriods.CheckAsync(from, cancellationToken) is { } locked)
        {
            return locked;
        }

        var enteredBy = currentUser.AccountId ?? throw new InvalidOperationException("Rates are entered by a logged-in user.");
        var jobType = new JobType { Name = name };
        jobType.Rates.Add(new PayRate { HourlyRate = rate, EffectiveFrom = from, EnteredByAccountId = enteredBy });
        jobTypes.Add(jobType);
        audit.Record(new AuditEntry(CreatedAction, nameof(JobType))
        {
            RecordId = jobType.Id,
            RecordCode = jobType.Name,
            After = new { jobType.Name, HourlyRate = rate, EffectiveFrom = from },
        });
        return await SaveNamedAsync(jobType, cancellationToken);
    }

    /// <summary>For a job type of either status; it keeps its rates and history.</summary>
    public async Task<Result<JobTypeSaved>> RenameAsync(Guid id, RenameJobTypeRequest request, CancellationToken cancellationToken)
    {
        if (await jobTypes.GetForEditAsync(id, cancellationToken) is not { } jobType)
        {
            return AppError.NotFound;
        }

        var check = new FieldCheck();
        var name = JobTypeFields.Name(check, request.Name);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await jobTypes.FindByNameAsync(name, id, cancellationToken) is { } other)
        {
            return AppError.AlreadyExists("Job type", other.Id);
        }

        if (await rowVersions.CheckAsync(jobType, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (jobType.Name == name)
        {
            return queries.Saved(jobType);
        }

        audit.Record(new AuditEntry("JobType.Renamed", nameof(JobType)) { RecordId = id, RecordCode = name, Before = new { jobType.Name }, After = new { Name = name } });
        jobType.Name = name;
        return await SaveNamedAsync(jobType, cancellationToken);
    }

    public Task<Result<JobTypeSaved>> DiscontinueAsync(Guid id, JobTypeStatusRequest request, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, request, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, cancellationToken);

    public Task<Result<JobTypeSaved>> ResumeAsync(Guid id, JobTypeStatusRequest request, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, request, StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, cancellationToken);

    /// <summary>No longer offered for profiles and work; what already has it keeps it (GBR-01).</summary>
    private async Task<Result<JobTypeSaved>> ChangeStatusAsync(Guid id, JobTypeStatusRequest request, int from, int to, CancellationToken cancellationToken)
    {
        if (await jobTypes.GetForEditAsync(id, cancellationToken) is not { } jobType)
        {
            return AppError.NotFound;
        }

        if (jobType.StatusId != from)
        {
            return queries.WrongStatus(jobType);
        }

        if (await rowVersions.CheckAsync(jobType, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        jobType.StatusId = to;
        audit.RecordStatusChange(nameof(JobType), jobType.Id, jobType.Name, from, to);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return queries.Saved(jobType);
    }

    /// <summary>A name taken by a parallel save passes the pre-check, and the unique index gives the same MSG-GEN-02.</summary>
    private async Task<Result<JobTypeSaved>> SaveNamedAsync(JobType jobType, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return queries.Saved(jobType);
        }
        catch (DuplicateRecordException)
        {
            return await jobTypes.FindByNameAsync(jobType.Name, jobType.Id, cancellationToken) is { } other
                ? AppError.AlreadyExists("Job type", other.Id)
                : AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
        }
    }
}
