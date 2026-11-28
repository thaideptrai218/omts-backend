using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WorkDayPlans;

namespace OMTS.Api.Controllers.Workforce;

/// <summary>
/// The work-day plans (Kế hoạch ngày làm, KNL000001): the people who do one kind of field work (Thu hoạch, Ươm giống, Thả giống) in one place on one day. The filters need the right in some zone; the services check the zone of
/// each plan. The Owner acts in every zone; a Zone Manager in its own zones. A harvest plan is made with its line of a harvest plan (<c>POST /api/harvest-plans</c>), never here. Every staffing route answers the whole plan
/// (<c>members</c> and <c>outsiders</c> included) so the screen replaces its copy.
/// </summary>
[ApiController]
[Authorize]
[Route("api/work-day-plans")]
public sealed class WorkDayPlanManagementController(WorkDayPlanCrudService plans, WorkDayPlanStaffingService staffing, WorkDayPlanOutsiderService outsiders) : ControllerBase
{
    /// <summary>The plans of the days <c>from</c> to <c>to</c> (at most 62 days) in the zones of <c>zoneIds</c> (the header zone filter; none = every zone the caller sees), by date then code, without members.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<WorkDayPlanItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] WorkDayPlanListQuery query, CancellationToken cancellationToken) =>
        (await plans.ListAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The plans of one day the caller can staff and that are not over: the plans to invite someone into.</summary>
    [HttpGet("day-options")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<WorkDayPlanItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DayOptions([FromQuery] WorkDayPlanDayQuery query, CancellationToken cancellationToken) =>
        (await plans.DayOptionsAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The people who can be in charge of a plan in the zone: the caller, and for the Owner the Zone Managers of the zone.</summary>
    [HttpGet("responsibles")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<WorkDayPlanResponsible>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Responsibles([FromQuery] Guid zoneId, CancellationToken cancellationToken) =>
        (await plans.ResponsiblesAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Warnings for the picker of employees on a day (approved leave, work elsewhere, already checked in); never a block.</summary>
    [HttpGet("availability")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<WorkDayAvailabilityRow>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Availability([FromQuery] DateOnly date, [FromQuery] Guid[]? employeeIds, CancellationToken cancellationToken) =>
        (await staffing.AvailabilityAsync(date, employeeIds, cancellationToken)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => (await plans.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>A nursery or seeding plan. A harvest plan is refused (409): it comes from a line of a harvest plan.</summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(SaveWorkDayPlanRequest request, CancellationToken cancellationToken) =>
        (await plans.CreateAsync(request, cancellationToken)).ToActionResult(Ok);

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveWorkDayPlanRequest request, CancellationToken cancellationToken) =>
        (await plans.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Delete, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelWorkDayPlanRequest request, CancellationToken cancellationToken) =>
        (await plans.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>"Hoàn thành" for a nursery or seeding plan that is Ready or running and whose day has come.</summary>
    [HttpPost("{id:guid}/complete")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(Guid id, CompleteWorkDayPlanRequest request, CancellationToken cancellationToken) =>
        (await plans.CompleteAsync(id, request, cancellationToken)).ToActionResult(Ok);

    // ---- People ----

    [HttpPost("{id:guid}/invite")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Invite(Guid id, InviteWorkDayPlanRequest request, CancellationToken cancellationToken) =>
        (await staffing.InviteAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/members/{memberId:guid}/confirm")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Confirm(Guid id, Guid memberId, ConfirmWorkDayMemberRequest request, CancellationToken cancellationToken) =>
        (await staffing.ConfirmAsync(id, memberId, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/members/{memberId:guid}/remove")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid memberId, RemoveWorkDayMemberRequest? request, CancellationToken cancellationToken) =>
        (await staffing.RemoveMemberAsync(id, memberId, request, cancellationToken)).ToActionResult(Ok);

    [HttpPut("{id:guid}/required-people")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetRequiredPeople(Guid id, SetWorkDayRequiredPeopleRequest request, CancellationToken cancellationToken) =>
        (await staffing.SetRequiredPeopleAsync(id, request, cancellationToken)).ToActionResult(Ok);

    // ---- People hired outside ----

    [HttpPost("{id:guid}/outsiders")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddOutsider(Guid id, SaveOutsiderHireRequest request, CancellationToken cancellationToken) =>
        (await outsiders.AddAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPut("{id:guid}/outsiders/{outsiderId:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateOutsider(Guid id, Guid outsiderId, SaveOutsiderHireRequest request, CancellationToken cancellationToken) =>
        (await outsiders.UpdateAsync(id, outsiderId, request, cancellationToken)).ToActionResult(Ok);

    [HttpDelete("{id:guid}/outsiders/{outsiderId:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveOutsider(Guid id, Guid outsiderId, CancellationToken cancellationToken) =>
        (await outsiders.RemoveAsync(id, outsiderId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Pays the line (cash voucher of the "Chi nhân công thuê ngoài" type). The right of paying wages: the Owner and the Accountant.</summary>
    [HttpPost("{id:guid}/outsiders/{outsiderId:guid}/pay")]
    [RequirePermission(PermissionFunctions.Payroll, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<WorkDayPlanItem>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PayOutsider(Guid id, Guid outsiderId, PayOutsiderHireRequest request, CancellationToken cancellationToken) =>
        (await outsiders.PayAsync(id, outsiderId, request, cancellationToken)).ToActionResult(Ok);
}
