using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Labor;
using OMTS.Application.Features.Labor;
namespace OMTS.Application.Features.Workforce;

public sealed class WorkforceSettingsService(IWorkforceRepository repository, AuditLogService audit, IUnitOfWork uow,
    PayPeriodLock payPeriods, ICooperativeRepository cooperatives)
{
    public Task<IReadOnlyList<AttendanceSettings>> ListAsync(CancellationToken ct) => repository.SettingsAsync(ct);
    public async Task<Result<AttendanceSettings>> SaveAsync(SaveAttendanceSettings input, CancellationToken ct)
    {
        if (input.EffectiveFrom == default || input.RoundingMinutes is not (0 or 15 or 30 or 60) ||
            (input.BreakFrom is null) != (input.BreakTo is null) || input.BreakTo <= input.BreakFrom)
            return AppError.Invalid("Attendance settings");
        if (await payPeriods.CheckAsync(input.EffectiveFrom, ct) is { } locked) return locked;
        if ((await repository.SettingsAsync(ct)).Any(s => s.EffectiveFrom == input.EffectiveFrom)) return AppError.Invalid("Effective date already used");
        if (await cooperatives.GetCurrentAsync(ct) is not { } cooperative) return AppError.NotFound;
        uow.MarkChanged(cooperative);
        var row = new AttendanceSettings { EffectiveFrom = input.EffectiveFrom, BreakFrom = input.BreakFrom, BreakTo = input.BreakTo, RoundingMinutes = input.RoundingMinutes };
        repository.AddSettings(row);
        audit.Record(new AuditEntry("AttendanceSettings.Saved", nameof(AttendanceSettings)) { RecordId = row.Id, After = input });
        await uow.SaveChangesAsync(ct);
        return (await repository.SettingsAsync(ct)).Single(s => s.Id == row.Id);
    }
    public async Task<Result<JobOption>> CreateJobAsync(SaveJobType input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200) return AppError.Invalid("Job type");
        var name = input.Name.Trim();
        if ((await repository.JobsAsync(ct)).Any(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return AppError.Invalid("Job type already exists");
        var row = new JobType { Name = name };
        repository.AddJob(row);
        audit.Record(new AuditEntry("JobType.Created", nameof(JobType)) { RecordId = row.Id, After = new { row.Name } });
        await uow.SaveChangesAsync(ct);
        return new JobOption(row.Id, row.Name);
    }
}
