using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor;

/// <summary>LABOR-11 step 1: the job types with their status, current rate and other rates; and the choices for a profile.</summary>
public sealed class JobTypeQueryService(IJobTypeRepository jobTypes, IStatusCatalog statusCatalog, TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    public async Task<Paged<JobTypeView>> SearchAsync(string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        // The upper bound keeps (page - 1) * pageSize inside int.
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var text = DisplayNames.Normalize(search);
        if (text.Length > JobTypeFields.MaxName)
        {
            return new Paged<JobTypeView>([], page, pageSize, 0);
        }

        var (items, total) = await jobTypes.SearchAsync(text, statusId, page, pageSize, cancellationToken);
        return new Paged<JobTypeView>(items.Select(View).ToList(), page, pageSize, total);
    }

    public async Task<Result<JobTypeView>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await jobTypes.GetForEditAsync(id, cancellationToken) is { } jobType ? View(jobType) : AppError.NotFound;

    public async Task<IReadOnlyList<JobTypeOption>> OptionsAsync(CancellationToken cancellationToken) =>
        (await jobTypes.ListInUseAsync(cancellationToken)).Select(j => new JobTypeOption(j.Id, j.Name)).ToList();

    public JobTypeView View(JobType jobType)
    {
        var current = PayRate.EffectiveOn(jobType.Rates, timeProvider.Today());
        return new JobTypeView(
            jobType.Id,
            jobType.Name,
            jobType.StatusId,
            statusCatalog.DisplayNameOf(jobType.StatusId),
            current is null ? null : RateView(current),
            jobType.Rates.Where(r => r != current).OrderByDescending(r => r.EffectiveFrom).Select(RateView).ToList(),
            RowVersionGuard.Encode(jobType.RowVersion));
    }

    /// <summary>The answer to a save: the view with MSG-GEN-05.</summary>
    public JobTypeSaved Saved(JobType jobType)
    {
        var saved = UserMessages.Saved("Job type");
        return new JobTypeSaved(View(jobType), saved.Text, saved.Code);
    }

    /// <summary>MSG-GEN-09: the job type is in the other status.</summary>
    public AppError WrongStatus(JobType jobType) =>
        AppError.Conflict(UserMessages.WrongStatus($"Job type {jobType.Name}", statusCatalog.DisplayNameOf(jobType.StatusId)));

    private static PayRateView RateView(PayRate rate) => new(rate.HourlyRate, rate.EffectiveFrom);
}
