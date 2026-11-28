using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// The people who harvest a plan (owner decisions of 26/11/2026). The Owner (and, for a plan the Zone Managers harvest, a Zone Manager of its zones) chooses employees, staff or seasonal workers; each is told and answers in the mobile
/// app (<see cref="HarvestPlanMyShiftService"/>); the plan view shows how many confirmed against the people needed. The Owner can confirm for an employee with a reason, take a person off, and add people hired on the spot outside the
/// system ("Bổ sung nhân công"), who need no confirmation. Open until the plan is Completed or Cancelled; never blocks starting the harvest.
/// </summary>
public sealed class HarvestPlanRosterService(
    IHarvestPlanRepository plans,
    IHarvestPlanStaffRepository staff,
    IEmployeeRepository employees,
    EmployeeCallerAccess callers,
    EmployeeService employeeService,
    HarvestPlanAccess access,
    HarvestPlanRosterViews views,
    HarvestPlanNotifier notifier,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    ICurrentUser user,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    // ---- Reading ----

    /// <summary>The roster of a plan the caller sees (404 otherwise): the Owner every plan, a Zone Manager the plans handed to Zone Managers with a line in its zones.</summary>
    public async Task<Result<HarvestPlanRoster>> GetAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !await access.MayReadAsync(plan, cancellationToken))
        {
            return AppError.NotFound;
        }

        return await views.BuildAsync(plan, await CanEditAsync(plan, cancellationToken), cancellationToken);
    }

    /// <summary>The roster of a plan the caller has loaded already (the staffing of the plan answers it with the crew of the lines).</summary>
    public async Task<HarvestPlanRoster> ViewAsync(HarvestPlan plan, CancellationToken cancellationToken) =>
        await views.BuildAsync(plan, await CanEditAsync(plan, cancellationToken), cancellationToken);

    // ---- Choosing the people ----

    /// <summary>
    /// Puts employees on the roster as Invited and tells each (HARVEST_SHIFT_INVITED); those already on it, in any state, are skipped. 404 for a plan the caller does not see; 403 for a caller who may not staff it;
    /// 409 MSG-GEN-09 once the plan is Completed or Cancelled; 400 MSG-GEN-01 <c>Employees</c> for an employee who does not exist, is not Working or is not one the caller sees.
    /// </summary>
    public async Task<Result<HarvestPlanRoster>> InviteAsync(Guid planId, InviteHarvestStaffRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForWriteAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        var added = await AddPeopleAsync(plan, request.EmployeeIds, cancellationToken);
        if (!added.IsSuccess)
        {
            return added.Error!;
        }

        await SaveAndNotifyAsync(plan, added.Value!, cancellationToken);
        return await views.BuildAsync(plan, true, cancellationToken);
    }

    /// <summary>
    /// The whole roster as the picker leaves it, with the number of people needed: listed employees not on the roster are invited, unlisted people who are not "external" are taken off, and <c>requiredPeople</c> is set (null clears it).
    /// 409 MSG06 for a stale <c>rowVersion</c> of the plan when one is sent.
    /// </summary>
    public async Task<Result<HarvestPlanRoster>> SaveAsync(Guid planId, SaveHarvestRosterRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForWriteAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        if (request.EmployeeIds is null)
        {
            return AppError.InvalidFields(["Employees"]);
        }

        if (!HarvestPlanRosterRules.ValidRequiredPeople(request.RequiredPeople))
        {
            return AppError.InvalidFields(["Required people"]);
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var wanted = request.EmployeeIds.Distinct().ToHashSet();
        var added = await AddPeopleAsync(plan, request.EmployeeIds, cancellationToken);
        if (!added.IsSuccess)
        {
            return added.Error!;
        }

        foreach (var row in plan.Staff.Where(r => r.State != HarvestPlanStaffState.External && !wanted.Contains(r.EmployeeId) && !added.Value!.Contains(r)).ToList())
        {
            RemoveRow(plan, row, null);
        }

        ChangeRequiredPeople(plan, request.RequiredPeople);
        await SaveAndNotifyAsync(plan, added.Value!, cancellationToken);
        return await views.BuildAsync(plan, true, cancellationToken);
    }

    /// <summary>Sets the number of people the plan needs (1 to 500, null clears it) while the plan is open; 400 MSG-GEN-01 <c>Required people</c> otherwise; 409 MSG06 for a stale <c>rowVersion</c>.</summary>
    public async Task<Result<HarvestPlanRoster>> SetRequiredAsync(Guid planId, SetRequiredPeopleRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForWriteAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        if (!HarvestPlanRosterRules.ValidRequiredPeople(request.RequiredPeople))
        {
            return AppError.InvalidFields(["Required people"]);
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        ChangeRequiredPeople(plan, request.RequiredPeople);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.BuildAsync(plan, true, cancellationToken);
    }

    /// <summary>
    /// Confirms for an employee who has not answered (or declined): State ConfirmedManually, with the reason (required, at most 500 characters), who and when; audited. A person already confirmed stays as is.
    /// 400 MSG-GEN-01 <c>Reason</c> without a reason; 404 for a row not on the plan; 409 MSG-GEN-09 for a person added outside the system (already counted).
    /// </summary>
    public async Task<Result<HarvestPlanRoster>> ConfirmAsync(Guid planId, Guid rowId, ConfirmHarvestStaffRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForWriteAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        var row = plan.Staff.FirstOrDefault(r => r.Id == rowId);
        if (row is null)
        {
            return AppError.NotFound;
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > HarvestPlanRosterRules.MaxReasonLength)
        {
            return AppError.InvalidFields(["Reason"]);
        }

        if (row.State == HarvestPlanStaffState.External)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Harvest plan staff", "External"));
        }

        if (row.State is HarvestPlanStaffState.Invited or HarvestPlanStaffState.Declined)
        {
            var before = row.State;
            row.State = HarvestPlanStaffState.ConfirmedManually;
            row.Reason = reason;
            row.RespondedAt = timeProvider.GetUtcNow();
            row.ConfirmedByAccountId = user.AccountId;
            audit.Record(new AuditEntry("HarvestPlan.StaffConfirmedManually", HarvestPlanService.RecordType)
            {
                RecordId = plan.Id,
                RecordCode = plan.Code,
                Before = new { Employee = row.Employee.FullName, State = before.ToString() },
                After = new { Employee = row.Employee.FullName, State = row.State.ToString() },
                Reason = reason,
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await views.BuildAsync(plan, true, cancellationToken);
    }

    /// <summary>Takes a person off the roster, in any state; the optional reason goes to the audit. A person added outside the system stays a seasonal worker of the cooperative.</summary>
    public async Task<Result<HarvestPlanRoster>> RemoveAsync(Guid planId, Guid rowId, RemoveHarvestStaffRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForWriteAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        var row = plan.Staff.FirstOrDefault(r => r.Id == rowId);
        if (row is null)
        {
            return AppError.NotFound;
        }

        var reason = request.Reason?.Trim();
        if (reason is { Length: > HarvestPlanRosterRules.MaxReasonLength })
        {
            return AppError.InvalidFields(["Reason"]);
        }

        RemoveRow(plan, row, string.IsNullOrEmpty(reason) ? null : reason);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.BuildAsync(plan, true, cancellationToken);
    }

    // ---- Bổ sung nhân công ----

    /// <summary>
    /// "Bổ sung nhân công": a person hired on the spot outside the system. A seasonal worker record is made for them (name and phone number required; address and note optional; no zone) with its login account by the rules of
    /// saving a seasonal worker, and they join the roster as External, counted as confirmed at once. 400 MSG-GEN-01 for an invalid field; 409 MSG-LABOR-03 for a phone number another working employee has (the answer names them: invite
    /// that employee instead); 409 MSG-LABOR-01 for a Zone Manager who did not send <c>ownerApproved</c>.
    /// </summary>
    public async Task<Result<HarvestPlanExternalAdded>> AddExternalAsync(Guid planId, AddExternalHarvestStaffRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForWriteAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            invalid.Add("Phone number");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var saved = await employeeService.CreateAsync(
            new SaveEmployeeRequest(
                request.FullName, request.PhoneNumber, null, request.Address, null, null, null, request.Note, [], null,
                EmployeeType: EmployeeType.SeasonalWorker, OwnerApproved: request.OwnerApproved),
            cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved.Error!;
        }

        var employee = (await employees.GetForEditAsync(saved.Value!.Employee.Id, cancellationToken))!;
        var now = timeProvider.GetUtcNow();
        var row = new HarvestPlanStaffMember
        {
            Plan = plan,
            Employee = employee,
            State = HarvestPlanStaffState.External,
            InvitedAt = now,
            RespondedAt = now,
            AddedByAccountId = user.AccountId,
        };
        staff.Add(row);
        audit.Record(new AuditEntry("HarvestPlan.StaffExternalAdded", HarvestPlanService.RecordType)
        {
            RecordId = plan.Id,
            RecordCode = plan.Code,
            After = new { Employee = employee.FullName, employee.Code, employee.PhoneNumber, employee.Address, employee.Notes },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var roster = await views.BuildAsync(plan, true, cancellationToken);
        return new HarvestPlanExternalAdded(roster, roster.People.First(p => p.Id == row.Id));
    }

    // ---- Building ----

    /// <summary>The plan with its roster, when the caller sees it (404), may staff it (403) and it is not over (409 MSG-GEN-09).</summary>
    private async Task<Result<HarvestPlan>> LoadForWriteAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !await access.MayReadAsync(plan, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await MayStaffAsync(plan, cancellationToken))
        {
            return AppError.Forbidden;
        }

        return HarvestPlanRosterRules.Open(plan.StatusId)
            ? plan
            : AppError.Conflict(UserMessages.PlanStaffingClosed(plan.Code, statusCatalog.DisplayNameOf(plan.StatusId)));
    }

    /// <summary>The Owner for any plan; a Zone Manager only for a plan the Zone Managers harvest, when it may harvest in one of the zones it sees of the plan.</summary>
    private async Task<bool> MayStaffAsync(HarvestPlan plan, CancellationToken cancellationToken)
    {
        if (await access.IsOwnerAsync(cancellationToken))
        {
            return true;
        }

        if (plan.Executor != HarvestPlanExecutor.ZoneManager)
        {
            return false;
        }

        foreach (var zoneId in (await access.VisibleLinesAsync(plan, cancellationToken)).Select(l => l.Raft.ZoneId).Distinct())
        {
            if (await access.MayHarvestAsync(zoneId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> CanEditAsync(HarvestPlan plan, CancellationToken cancellationToken) =>
        HarvestPlanRosterRules.Open(plan.StatusId) && await MayStaffAsync(plan, cancellationToken);

    /// <summary>Adds the employees not on the roster as Invited rows (audited); the ones already on it are skipped. The rows are returned in the order of the request.</summary>
    private async Task<Result<IReadOnlyList<HarvestPlanStaffMember>>> AddPeopleAsync(HarvestPlan plan, IReadOnlyList<Guid>? employeeIds, CancellationToken cancellationToken)
    {
        if (employeeIds is null || employeeIds.Count > HarvestPlanRosterRules.MaxPeoplePerRequest)
        {
            return AppError.InvalidFields(["Employees"]);
        }

        var caller = await callers.GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var added = new List<HarvestPlanStaffMember>();
        foreach (var employeeId in employeeIds.Distinct().Where(id => plan.Staff.All(r => r.EmployeeId != id)))
        {
            var employee = await employees.GetForEditAsync(employeeId, cancellationToken);
            if (employee is null || employee.StatusId != StatusIds.Employee.Working || !caller.Sees(employee))
            {
                return AppError.InvalidFields(["Employees"]);
            }

            var row = new HarvestPlanStaffMember { Plan = plan, Employee = employee, State = HarvestPlanStaffState.Invited, InvitedAt = now, AddedByAccountId = user.AccountId };
            staff.Add(row);
            added.Add(row);
            audit.Record(new AuditEntry("HarvestPlan.StaffInvited", HarvestPlanService.RecordType)
            {
                RecordId = plan.Id,
                RecordCode = plan.Code,
                After = new { Employee = employee.FullName, employee.Code, State = row.State.ToString() },
            });
        }

        return added;
    }

    private void RemoveRow(HarvestPlan plan, HarvestPlanStaffMember row, string? reason)
    {
        staff.Remove(row);
        audit.Record(new AuditEntry("HarvestPlan.StaffRemoved", HarvestPlanService.RecordType)
        {
            RecordId = plan.Id,
            RecordCode = plan.Code,
            Before = new { Employee = row.Employee.FullName, row.Employee.Code, State = row.State.ToString() },
            Reason = reason,
        });
    }

    private void ChangeRequiredPeople(HarvestPlan plan, int? requiredPeople)
    {
        if (plan.RequiredPeople == requiredPeople)
        {
            return;
        }

        audit.Record(new AuditEntry("HarvestPlan.RequiredPeopleChanged", HarvestPlanService.RecordType)
        {
            RecordId = plan.Id,
            RecordCode = plan.Code,
            Before = new { plan.RequiredPeople },
            After = new { RequiredPeople = requiredPeople },
        });
        plan.RequiredPeople = requiredPeople;
    }

    /// <summary>Tells the employees newly invited (those with an account), then saves everything in one commit.</summary>
    private async Task SaveAndNotifyAsync(HarvestPlan plan, IReadOnlyList<HarvestPlanStaffMember> added, CancellationToken cancellationToken)
    {
        var accountIds = added.Select(r => r.Employee.AccountId).OfType<Guid>().ToList();
        if (accountIds.Count > 0)
        {
            await notifier.StaffInvitedAsync(plan, accountIds, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
