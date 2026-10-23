using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Workforce;

/// <summary>
/// LAB-02 – seasonal workers and crew leaders (no login accounts), cooperative-wide. Deactivated instead of deleted;
/// a Discontinued worker cannot be chosen for new work or attendance, history stays unchanged.
/// </summary>
public sealed class SeasonalWorkerService(
    IWorkforceRepository workforce,
    IEmployeeRepository employees,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public const string ObjectName = "Seasonal worker";

    public async Task<Paged<WorkerListItem>> SearchAsync(
        string? search, Guid? teamId, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));
        var (items, total) = await workforce.SearchWorkersAsync(search, teamId, statusId, page, pageSize, cancellationToken);
        return new Paged<WorkerListItem>(
            items.Select(w => new WorkerListItem(w.Id, w.FullName, w.PhoneNumber, w.CitizenId, Team(w.Contractor), w.StatusId, statusCatalog.DisplayNameOf(w.StatusId))).ToList(),
            page, pageSize, total);
    }

    public async Task<Result<WorkerView>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await workforce.GetWorkerAsync(id, cancellationToken) is { } worker ? await ViewAsync(worker, cancellationToken) : AppError.NotFound;

    public async Task<Result<WorkerView>> CreateAsync(SaveWorkerRequest request, CancellationToken cancellationToken)
    {
        var checkedInput = await ValidateAsync(request, null, cancellationToken);
        if (!checkedInput.IsSuccess)
        {
            return checkedInput.Error!;
        }

        var input = checkedInput.Value!;
        var worker = new SeasonalWorker { FullName = input.FullName, PhoneNumber = input.PhoneNumber, CitizenId = input.CitizenId, ContractorId = request.TeamId };
        worker.EmployeeId = request.EmployeeId ?? await SamePersonEmployeeAsync(input, cancellationToken);
        workforce.AddWorker(worker);
        audit.Record(new AuditEntry("SeasonalWorker.Created", nameof(SeasonalWorker)) { RecordId = worker.Id, After = Snapshot(worker) });
        return await SaveAsync(worker, input, cancellationToken);
    }

    public async Task<Result<WorkerView>> UpdateAsync(Guid id, SaveWorkerRequest request, CancellationToken cancellationToken)
    {
        var worker = await workforce.GetWorkerAsync(id, cancellationToken);
        if (worker is null)
        {
            return AppError.NotFound;
        }

        var checkedInput = await ValidateAsync(request, worker, cancellationToken);
        if (!checkedInput.IsSuccess)
        {
            return checkedInput.Error!;
        }

        if (await rowVersions.CheckAsync(worker, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var input = checkedInput.Value!;
        var before = Snapshot(worker);
        if (worker.ContractorId != request.TeamId)
        {
            // A leader who leaves the team is no longer its leader; past attendance keeps the team of each day.
            foreach (var led in await workforce.GetTeamsLedByAsync(worker.Id, cancellationToken))
            {
                led.LeaderWorkerId = null;
                audit.Record(new AuditEntry("Contractor.Updated", nameof(Contractor))
                {
                    RecordId = led.Id, Before = new { LeaderWorkerId = (Guid?)worker.Id }, After = new { LeaderWorkerId = (Guid?)null },
                });
            }
        }

        (worker.FullName, worker.PhoneNumber, worker.CitizenId, worker.ContractorId, worker.EmployeeId) =
            (input.FullName, input.PhoneNumber, input.CitizenId, request.TeamId, request.EmployeeId);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(worker));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("SeasonalWorker.Updated", nameof(SeasonalWorker)) { RecordId = worker.Id, Before = changedBefore, After = changedAfter });
        }

        return await SaveAsync(worker, input, cancellationToken);
    }

    /// <summary>In Use → Discontinued (never blocked) or back.</summary>
    public async Task<Result<WorkerView>> ChangeStatusAsync(Guid id, int targetStatusId, CancellationToken cancellationToken)
    {
        var worker = await workforce.GetWorkerAsync(id, cancellationToken);
        if (worker is null)
        {
            return AppError.NotFound;
        }

        if (UsageStatusChange.SameStatus(statusCatalog, ObjectName, worker.StatusId, targetStatusId) is { } same)
        {
            return same;
        }

        audit.RecordStatusChange(nameof(SeasonalWorker), worker.Id, null, worker.StatusId, targetStatusId);
        worker.StatusId = targetStatusId;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(worker, cancellationToken);
    }

    private async Task<Result<WorkerInput>> ValidateAsync(SaveWorkerRequest request, SeasonalWorker? current, CancellationToken cancellationToken)
    {
        var (input, invalid) = WorkerInput.From(request);
        if (input is null)
        {
            return AppError.InvalidFields(invalid);
        }

        if (request.TeamId is { } teamId && teamId != current?.ContractorId
            && await workforce.GetTeamAsync(teamId, cancellationToken) is not { StatusId: StatusIds.Usage.InUse })
        {
            return AppError.Invalid("Team");
        }

        if (request.EmployeeId is { } employeeId && employeeId != current?.EmployeeId
            && await employees.GetByIdAsync(employeeId, cancellationToken) is null)
        {
            return AppError.Invalid("Employee");
        }

        if (await workforce.FindSameWorkerAsync(input.FullName, input.PhoneNumber, input.CitizenId, current?.Id, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id);
        }

        return input;
    }

    private async Task<Guid?> SamePersonEmployeeAsync(WorkerInput input, CancellationToken cancellationToken)
    {
        if (input.PhoneNumber is null)
        {
            return null;
        }

        var matches = await workforce.FindEmployedSamePersonAsync(input.FullName, input.PhoneNumber, cancellationToken);
        return matches.Count == 1 ? matches[0].Id : null;
    }

    // A parallel save of the same person passed the pre-check; the unique index refuses it and it gets the same MSG03.
    private async Task<Result<WorkerView>> SaveAsync(SeasonalWorker worker, WorkerInput input, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            if (await workforce.FindSameWorkerAsync(input.FullName, input.PhoneNumber, input.CitizenId, worker.Id, cancellationToken) is { } same)
            {
                return AppError.AlreadyExists(ObjectName, same.Id);
            }

            throw;
        }

        return await ViewAsync((await workforce.GetWorkerAsync(worker.Id, cancellationToken))!, cancellationToken);
    }

    private async Task<WorkerView> ViewAsync(SeasonalWorker w, CancellationToken cancellationToken)
    {
        var leads = (await workforce.GetTeamsLedByAsync(w.Id, cancellationToken)).Count > 0;
        return new WorkerView(
            w.Id, w.FullName, w.PhoneNumber, w.CitizenId, Team(w.Contractor),
            w.Employee is { } e ? new LinkedEmployeeRef(e.Id, e.FullName) : null,
            leads, w.StatusId, statusCatalog.DisplayNameOf(w.StatusId), RowVersionGuard.Encode(w.RowVersion));
    }

    private static TeamRef? Team(Contractor? team) => team is null ? null : new TeamRef(team.Id, team.Name);

    private static object Snapshot(SeasonalWorker w) => new { w.FullName, w.PhoneNumber, w.CitizenId, TeamId = w.ContractorId, w.EmployeeId };
}
