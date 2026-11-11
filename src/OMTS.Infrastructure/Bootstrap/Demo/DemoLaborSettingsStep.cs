using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Labor;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// LABOR-11, LABOR-15: job types with rates, one with an earlier rate, one with a rate still to come and one discontinued, and
/// two versions of the attendance settings; before the employees, whose profiles name the job types. The payroll settings stay
/// as set up (start day 1, automatic payroll off). The payroll sheets come with <see cref="DemoPayrollStep"/>, after the attendance they
/// are calculated from. A job type is matched by its name and by the audit row of its adding, which a rename leaves as it is; settings
/// are added only when there are none, and this order keeps the rates before the lock. Dates are relative to today, in whole months, so
/// no month end moves them.
/// </summary>
internal sealed class DemoLaborSettingsStep : IDemoSeedStep
{
    public const string Harvesting = "Thu hoạch";
    public const string HangingSeed = "Treo giống";
    public const string CleaningLines = "Vệ sinh dây giàn";
    private const string OldPacking = "Đóng gói tại bè";

    public string Name => "job types and attendance settings";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var thisMonth = new DateOnly(context.Today.Year, context.Today.Month, 1);
        var created = 0;
        created += await AddJobTypeAsync(context, Harvesting, 38_000, thisMonth.AddMonths(-6), (40_000, thisMonth.AddMonths(-3)), cancellationToken);
        created += await AddJobTypeAsync(context, HangingSeed, 35_000, thisMonth.AddMonths(-6), (36_000, thisMonth.AddMonths(1)), cancellationToken);
        created += await AddJobTypeAsync(context, CleaningLines, 30_000, thisMonth.AddMonths(-6), null, cancellationToken);
        created += await AddOldJobTypeAsync(context, thisMonth, cancellationToken);
        created += await AddAttendanceSettingsAsync(context, thisMonth, cancellationToken);
        return created;
    }

    /// <summary>
    /// The demo job types a new profile may name: under whatever name they have now (a person may have renamed them), and only
    /// those In Use, as the profile would be refused one a person discontinued (GBR-01).
    /// </summary>
    public static async Task<List<Guid>> JobTypesInUseAsync(DemoSeedContext context, string[] names, CancellationToken cancellationToken)
    {
        var added = await context.Db.AuditEvents
            .Where(e => e.Action == JobTypeService.CreatedAction && e.RecordCode != null && names.Contains(e.RecordCode))
            .Select(e => e.RecordId!.Value).ToListAsync(cancellationToken);
        return await context.Db.JobTypes
            .Where(j => j.StatusId == StatusIds.Usage.InUse && (names.Contains(j.Name) || added.Contains(j.Id)))
            .Select(j => j.Id).ToListAsync(cancellationToken);
    }

    /// <summary>Whether an earlier run, or a person, has already added it. Then a rerun leaves it: its rename, rates and status are real use.</summary>
    private static async Task<bool> AlreadyAddedAsync(DemoSeedContext context, string name, CancellationToken cancellationToken) =>
        await context.Get<IJobTypeRepository>().FindByNameAsync(name, null, cancellationToken) is not null
        || await context.Db.AuditEvents.AnyAsync(e => e.Action == JobTypeService.CreatedAction && e.RecordCode == name, cancellationToken);

    private static async Task<int> AddJobTypeAsync(
        DemoSeedContext context, string name, decimal rate, DateOnly from, (decimal Rate, DateOnly From)? second, CancellationToken cancellationToken)
    {
        if (await AlreadyAddedAsync(context, name, cancellationToken))
        {
            return 0;
        }

        var saved = DemoSeedContext.Ensure(await context.Get<JobTypeService>().AddAsync(new AddJobTypeRequest(name, rate, from), cancellationToken), $"job type {name}");
        if (second is { } next)
        {
            DemoSeedContext.Ensure(
                await context.Get<PayRateService>().ChangeRateAsync(saved.JobType.Id, new ChangeRateRequest(next.Rate, next.From, saved.JobType.RowVersion), cancellationToken),
                $"second rate of {name}");
        }

        return 1;
    }

    private static async Task<int> AddOldJobTypeAsync(DemoSeedContext context, DateOnly thisMonth, CancellationToken cancellationToken)
    {
        var added = await AddJobTypeAsync(context, OldPacking, 28_000, thisMonth.AddMonths(-6), null, cancellationToken);
        if (added == 0)
        {
            return 0;
        }

        var jobType = await context.Db.JobTypes.SingleAsync(j => j.Name == OldPacking, cancellationToken);
        DemoSeedContext.Ensure(
            await context.Get<JobTypeService>().DiscontinueAsync(jobType.Id, new JobTypeStatusRequest(RowVersionGuard.Encode(jobType.RowVersion)), cancellationToken),
            $"discontinuing {OldPacking}");
        return added;
    }

    private static async Task<int> AddAttendanceSettingsAsync(DemoSeedContext context, DateOnly thisMonth, CancellationToken cancellationToken)
    {
        if (await context.Db.AttendanceSettingsVersions.AnyAsync(cancellationToken))
        {
            return 0;
        }

        var service = context.Get<AttendanceSettingsService>();
        var (breakStart, breakEnd) = ("11:30", "12:30");
        var first = DemoSeedContext.Ensure(await service.GetAsync(cancellationToken), "reading the attendance settings");
        var saved = DemoSeedContext.Ensure(
            await service.SaveAsync(new SaveAttendanceSettingsRequest(breakStart, breakEnd, AttendanceRoundingUnit.Minutes30, thisMonth.AddMonths(-6), first.RowVersion), cancellationToken),
            "the earlier attendance settings");
        DemoSeedContext.Ensure(
            await service.SaveAsync(new SaveAttendanceSettingsRequest(breakStart, breakEnd, AttendanceRoundingUnit.Minutes15, thisMonth.AddMonths(-3), saved.Settings.RowVersion), cancellationToken),
            "the attendance settings");
        return 1;
    }
}
