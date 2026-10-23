using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Workforce;

/// <summary>
/// LAB-02 – teams (the Contractor entity): a crew or contractor team led by a crew leader who is one of its members.
/// Members are workers whose current team it is. Deactivation is blocked while a team payslip is not fully paid (MSG11).
/// </summary>
public sealed class TeamService(
    IWorkforceRepository workforce,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public const string ObjectName = "Team";

    public async Task<Paged<TeamListItem>> SearchAsync(string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));
        var (items, total) = await workforce.SearchTeamsAsync(search, statusId, page, pageSize, cancellationToken);
        return new Paged<TeamListItem>(
            items.Select(t => new TeamListItem(
                t.Id, t.Name, Leader(t), t.PhoneNumber ?? t.Leader?.PhoneNumber, t.Members.Count, t.StatusId, statusCatalog.DisplayNameOf(t.StatusId))).ToList(),
            page, pageSize, total);
    }

    public async Task<Result<TeamView>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await workforce.GetTeamAsync(id, cancellationToken) is { } team ? View(team) : AppError.NotFound;

    public async Task<Result<TeamView>> CreateAsync(SaveTeamRequest request, CancellationToken cancellationToken)
    {
        var team = new Contractor();
        if (await ValidateAsync(request, team, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        workforce.AddTeam(team);
        audit.Record(new AuditEntry("Contractor.Created", nameof(Contractor)) { RecordId = team.Id, After = Snapshot(team) });
        return await SaveAsync(team, cancellationToken);
    }

    public async Task<Result<TeamView>> UpdateAsync(Guid id, SaveTeamRequest request, CancellationToken cancellationToken)
    {
        var team = await workforce.GetTeamAsync(id, cancellationToken);
        if (team is null)
        {
            return AppError.NotFound;
        }

        if (await rowVersions.CheckAsync(team, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(team);
        if (await ValidateAsync(request, team, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(team));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Contractor.Updated", nameof(Contractor)) { RecordId = team.Id, Before = changedBefore, After = changedAfter });
        }

        return await SaveAsync(team, cancellationToken);
    }

    /// <summary>In Use → Discontinued (blocked by unpaid team payslips, MSG11) or back.</summary>
    public async Task<Result<TeamView>> ChangeStatusAsync(Guid id, int targetStatusId, CancellationToken cancellationToken)
    {
        var team = await workforce.GetTeamAsync(id, cancellationToken);
        if (team is null)
        {
            return AppError.NotFound;
        }

        if (UsageStatusChange.SameStatus(statusCatalog, ObjectName, team.StatusId, targetStatusId) is { } same)
        {
            return same;
        }

        if (targetStatusId == StatusIds.Usage.Discontinued)
        {
            var periods = await workforce.GetUnsettledPayslipPeriodsAsync(team.Id, cancellationToken);
            if (periods.Count > 0)
            {
                var list = "payslips " + string.Join(", ", periods.Select(PayrollPeriod.LabelOf));
                return AppError.Conflict(UserMessages.StillInUse(periods.Count, list), new Dictionary<string, object?> { ["periods"] = periods });
            }
        }

        audit.RecordStatusChange(nameof(Contractor), team.Id, team.Name, team.StatusId, targetStatusId);
        team.StatusId = targetStatusId;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(team);
    }

    /// <summary>Checks the request and applies it to the team; MSG01/MSG03 when invalid.</summary>
    private async Task<AppError?> ValidateAsync(SaveTeamRequest request, Contractor team, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        var name = LaborInputRules.Required(request.Name, 100, "Name", invalid);
        string? phone = null;
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber) && (phone = PhoneNumbers.Normalize(request.PhoneNumber)) is null)
        {
            invalid.Add("Phone number");
        }

        var otherContacts = LaborInputRules.Optional(request.OtherContacts, 500, "Other contacts", invalid);
        if (request.LeaderWorkerId is { } leaderId && team.Members.All(m => m.Id != leaderId))
        {
            invalid.Add("Leader");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await workforce.FindTeamByNameAsync(name!, team.Id, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id);
        }

        (team.Name, team.PhoneNumber, team.OtherContacts, team.LeaderWorkerId) = (name!, phone, otherContacts, request.LeaderWorkerId);
        team.Leader = request.LeaderWorkerId is { } id ? team.Members.First(m => m.Id == id) : null;
        return null;
    }

    private async Task<Result<TeamView>> SaveAsync(Contractor team, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            if (await workforce.FindTeamByNameAsync(team.Name, team.Id, cancellationToken) is { } same)
            {
                return AppError.AlreadyExists(ObjectName, same.Id);
            }

            throw;
        }

        return View(team);
    }

    private TeamView View(Contractor t) => new(
        t.Id, t.Name, Leader(t), t.PhoneNumber, t.PhoneNumber ?? t.Leader?.PhoneNumber, t.OtherContacts, t.StatusId,
        statusCatalog.DisplayNameOf(t.StatusId),
        t.Members.OrderBy(m => m.FullName).Select(m => new TeamMember(m.Id, m.FullName, m.PhoneNumber, statusCatalog.DisplayNameOf(m.StatusId))).ToList(),
        RowVersionGuard.Encode(t.RowVersion));

    private static WorkerRef? Leader(Contractor t) => t.Leader is { } leader ? new WorkerRef(leader.Id, leader.FullName) : null;

    private static object Snapshot(Contractor t) => new { t.Name, t.LeaderWorkerId, t.PhoneNumber, t.OtherContacts };
}
