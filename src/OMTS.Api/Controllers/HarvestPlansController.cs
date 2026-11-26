using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest.Plans;

namespace OMTS.Api.Controllers;

/// <summary>
/// The harvest plans (Kế hoạch thu hoạch, KHT000001). A plan spans the rafts of its lines, of any zone. The filters need the right in some zone; the services check the zones of the lines. The Owner makes, changes,
/// assigns, completes and cancels a plan; a Zone Manager reads the plans assigned that have a line in its zones (never a Draft) and sees only those lines, receives the plan (Nhận), records lots from them with <c>POST /api/rafts/{raftId}/harvest-lots</c> (<c>harvestPlanLineId</c>) and leaves lanes out ("Không thu") and hands the plan over (Bàn giao) once every line is done. Flow: Draft, Assigned (Owner), Received (Zone Manager), Harvesting (first lot or lane left out), HandedOver (Zone Manager), Completed (Owner).
/// </summary>
[ApiController]
[Route("api/harvest-plans")]
[Authorize]
public sealed class HarvestPlansController(
    HarvestPlanService plans, HarvestPlanReadService reads, HarvestPlanLaneSkipService skips, HarvestPlanStaffingService staffing,
    HarvestPlanHandoverService handover, HarvestPlanRosterService roster) : ControllerBase
{
    /// <summary>The plans the caller may see, code descending, with filters and paging. A Zone Manager sees only the plans handed over with a line in its zones (and only those lines).</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] HarvestPlanListQuery query, CancellationToken cancellationToken) =>
        (await reads.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The code the next plan would get (KHT000001); not reserved.</summary>
    [HttpGet("next-code")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanNextCode>(StatusCodes.Status200OK)]
    public async Task<IActionResult> NextCode(CancellationToken cancellationToken) => (await reads.NextCodeAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The rafts a plan can be made for (Owner): In Use, with an Open cultivation cycle and a lane that can be harvested, each with its zone, the product of its cycle and its lanes (Free, InAnotherPlan with the plan
    /// code, or Harvested). <c>zoneId</c> narrows it to one zone; without it every zone.
    /// </summary>
    [HttpGet("eligible-rafts")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<EligibleRafts>(StatusCodes.Status200OK)]
    public async Task<IActionResult> EligibleRafts([FromQuery] Guid? zoneId, CancellationToken cancellationToken) => (await reads.EligibleRaftsAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The plans Assigned, Received or Harvesting in the zones the caller harvests: what the harvest screen records from. Each carries its lines, lanes and the buttons the caller may press.</summary>
    [HttpGet("assigned")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlansToHarvest>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Assigned(CancellationToken cancellationToken) => (await reads.InProgressAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>The form of a new plan made from an order (Owner): its pickup time and products with the cages worked out. 409 MSG-GEN-09 when the order is Completed or Cancelled.</summary>
    [HttpGet("order-draft/{orderId:guid}")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanOrderDraft>(StatusCodes.Status200OK)]
    public async Task<IActionResult> OrderDraft(Guid orderId, CancellationToken cancellationToken) => (await reads.OrderDraftAsync(orderId, cancellationToken)).ToActionResult(Ok);

    /// <summary>One plan with its lines (each lane Planned, Harvested or Skipped with the lot code), the orders it serves and the buttons the caller may press (<c>allowedActions</c>).</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => (await reads.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>The plan with this code (regardless of letter case): the link <c>?code=KHT000001</c> opens it.</summary>
    [HttpGet("by-code/{code}")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByCode(string code, CancellationToken cancellationToken) => (await reads.GetByCodeAsync(code, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Saves a Draft plan. 409 MSG-GEN-02 for a code taken (field Code); 409 MSG-PLAN-02 for a lane that waits in another open plan; 400 MSG-GEN-01 names every invalid field (a line is named "Line 2 lanes",
    /// the orders "Sales orders").
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveHarvestPlanRequest request, CancellationToken cancellationToken) =>
        (await plans.CreateAsync(request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>
    /// Replaces the header, the orders and the lines of a plan that is not over (the row version is required; lines carry their <c>id</c>). After the hand-over a line that has its lot or a lane left out must come
    /// back unchanged (409 MSG-PLAN-03); other lines can change and lines can be added.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveHarvestPlanRequest request, CancellationToken cancellationToken) =>
        (await plans.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>"Giao QLV": hands a Draft to the Zone Managers of every zone of its lines (the Owners for a zone with none), who are told (MSG-PLAN-05). Every raft needs an Open cycle. Body <c>{}</c>.</summary>
    [HttpPost("{id:guid}/assign")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Assign(Guid id, HarvestPlanActionRequest request, CancellationToken cancellationToken) =>
        (await plans.AssignAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>"Nhận": a Zone Manager (HARVESTS Create in a zone of the plan; the Owner too) receives an Assigned plan; there is no refusal. 409 MSG-GEN-09 for another status. Body <c>{}</c> or <c>{"rowVersion"}</c>.</summary>
    [HttpPost("{id:guid}/receive")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Receive(Guid id, HarvestPlanActionRequest request, CancellationToken cancellationToken) =>
        (await handover.ReceiveAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>"Bàn giao": a Zone Manager hands over a Harvesting plan once every line of the plan has its lot or all its lanes left out; the Owners are told. 409 MSG-GEN-09 for another status or lines still to harvest. Body <c>{}</c>.</summary>
    [HttpPost("{id:guid}/hand-over")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> HandOver(Guid id, HarvestPlanActionRequest request, CancellationToken cancellationToken) =>
        (await handover.HandOverAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>"Hoàn thành": the Owner alone completes a plan a Zone Manager handed over (HandedOver). 409 MSG-GEN-09 for another status. The system never completes a plan. Body <c>{}</c>.</summary>
    [HttpPost("{id:guid}/finish")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Finish(Guid id, HarvestPlanActionRequest request, CancellationToken cancellationToken) =>
        (await plans.FinishAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Cancels a Draft, an Assigned or a Received plan with no lot, with a reason. 409 MSG-PLAN-04 for a plan that has started (hand it over and complete it instead).</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Delete, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelHarvestPlanRequest request, CancellationToken cancellationToken) =>
        (await plans.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The "Nhân công" of a plan: for each line the caller sees (every line for the Owner, the lines of its zones for a Zone Manager) the employees assigned to it, with <c>checkedIn</c> and the <c>assignmentId</c>,
    /// and <c>canEdit</c>: the caller may choose the crew now (the Owner on a Draft or Assigned plan, a Zone Manager of the line's zone on an Assigned, Received or Harvesting one, nobody on a HandedOver, Completed or Cancelled plan or after the day).
    /// </summary>
    [HttpGet("{id:guid}/staffing")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanStaffing>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Staffing(Guid id, CancellationToken cancellationToken)
    {
        var crew = await staffing.GetAsync(id, cancellationToken);
        if (!crew.IsSuccess)
        {
            return crew.ToActionResult(Ok);
        }

        // The roster of the whole plan (the people chosen and their answers) comes with the crew of the lines.
        var people = await roster.GetAsync(id, cancellationToken);
        return Ok(people.IsSuccess ? crew.Value! with { Roster = people.Value } : crew.Value);
    }

    /// <summary>
    /// Sets the whole crew of a line (<c>employeeIds</c>, <c>moveExisting</c>, <c>rowVersion</c> of the plan): employees not listed are taken off (409 MSG-LABOR-07 for one already checked in), new ones get a work assignment
    /// (Thu hoạch, the raft of the line, the day of the plan) by the rules of the work assignments (409 MSG-LABOR-06 for one on another raft that day, unless <c>moveExisting</c>). 409 MSG-GEN-09 once the plan is over, after its day or
    /// with no job type Thu hoạch in use. Answers the line.
    /// </summary>
    [HttpPut("{id:guid}/lines/{lineId:guid}/staffing")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanStaffingLine>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveStaffing(Guid id, Guid lineId, SaveHarvestPlanLineStaffingRequest request, CancellationToken cancellationToken) =>
        (await staffing.SaveLineAsync(id, lineId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The people chosen to harvest the plan with their answers and the people needed (owner decisions of 26/11/2026): each row is <c>Invited</c> (waiting for the employee in the mobile app), <c>Confirmed</c> (the employee accepted),
    /// <c>ConfirmedManually</c> (the Owner confirmed for them, with a reason), <c>Declined</c> or <c>External</c> (added outside the system, counted as confirmed). <c>summary</c> carries <c>requiredPeople</c>, <c>confirmed</c> (accepted and
    /// confirmed manually), <c>invited</c>, <c>declined</c>, <c>external</c>, <c>missing</c> and <c>ready</c> (nobody missing; false is a warning, never a block). Light enough to poll every few seconds: the answer has an <c>ETag</c>
    /// (<c>rosterVersion</c>) and a request with <c>If-None-Match</c> of it gets 304 while nothing changed. The Owner reads any plan; a Zone Manager the plans handed to Zone Managers with a line in its zones.
    /// </summary>
    [HttpGet("{id:guid}/staffing/roster")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanRoster>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<IActionResult> Roster(Guid id, CancellationToken cancellationToken)
    {
        var result = await roster.GetAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.ToActionResult(Ok);
        }

        var etag = "\"" + result.Value!.RosterVersion + "\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, no-cache";
        return Request.Headers.IfNoneMatch.ToString() == etag ? StatusCode(StatusCodes.Status304NotModified) : Ok(result.Value);
    }

    /// <summary>The whole roster as the picker leaves it (<c>employeeIds</c>) with the people needed (<c>requiredPeople</c>, null clears it) and the <c>rowVersion</c> of the plan when sent: new employees are invited and told, unlisted people (not "external") are taken off. Answers the roster.</summary>
    [HttpPut("{id:guid}/staffing/roster")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanRoster>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveRoster(Guid id, SaveHarvestRosterRequest request, CancellationToken cancellationToken) =>
        (await roster.SaveAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Chooses employees (<c>employeeIds</c>, staff and seasonal workers): those not on the roster become <c>Invited</c> and each gets a notification HARVEST_SHIFT_INVITED; the others are skipped. Answers the roster. 409 MSG-GEN-09 once the plan is over.</summary>
    [HttpPost("{id:guid}/staffing/invite")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanRoster>(StatusCodes.Status200OK)]
    public async Task<IActionResult> InviteStaff(Guid id, InviteHarvestStaffRequest request, CancellationToken cancellationToken) =>
        (await roster.InviteAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Sets the people the plan needs (<c>requiredPeople</c> 1 to 500, null clears it; <c>rowVersion</c> of the plan when sent) at any time until the plan is Completed or Cancelled. Not enforced anywhere: the plan only reports <c>ready</c>. Answers the roster.</summary>
    [HttpPut("{id:guid}/staffing/required")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanRoster>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetRequiredPeople(Guid id, SetRequiredPeopleRequest request, CancellationToken cancellationToken) =>
        (await roster.SetRequiredAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>"Bổ sung nhân công": a person hired on the spot (<c>fullName</c>, <c>phoneNumber</c> required; <c>note</c>, <c>address</c> optional; a Zone Manager also sends <c>ownerApproved</c>). A seasonal worker record and its login are made (the answer carries <c>username</c>, the fixed first <c>temporaryPassword</c> once and <c>fixedPassword</c>) and the person joins the roster as <c>External</c>, counted as confirmed at once.</summary>
    [HttpPost("{id:guid}/staffing/external")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanExternalAdded>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddExternalStaff(Guid id, AddExternalHarvestStaffRequest request, CancellationToken cancellationToken) =>
        (await roster.AddExternalAsync(id, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>Confirms on behalf of an employee (<c>reason</c> required, audited with who and when): the row becomes <c>ConfirmedManually</c>. 400 MSG-GEN-01 <c>Reason</c> without a reason. Answers the roster.</summary>
    [HttpPost("{id:guid}/staffing/{rowId:guid}/confirm")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanRoster>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfirmStaff(Guid id, Guid rowId, ConfirmHarvestStaffRequest request, CancellationToken cancellationToken) =>
        (await roster.ConfirmAsync(id, rowId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Takes a person off the roster (<c>reason</c> optional). Answers the roster.</summary>
    [HttpPost("{id:guid}/staffing/{rowId:guid}/remove")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanRoster>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveStaff(Guid id, Guid rowId, RemoveHarvestStaffRequest request, CancellationToken cancellationToken) =>
        (await roster.RemoveAsync(id, rowId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>"Không thu": leaves lanes of a line out with a reason (<c>laneNumbers</c>, <c>reason</c>); the Owners are told (MSG-PLAN-06). While the plan is Received or Harvesting and the line has no lot (409 MSG-PLAN-03).</summary>
    [HttpPost("{id:guid}/lines/{lineId:guid}/skip-lanes")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SkipLanes(Guid id, Guid lineId, SkipHarvestPlanLanesRequest request, CancellationToken cancellationToken) =>
        (await skips.SkipAsync(id, lineId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Puts lanes left out back (<c>laneNumbers</c>), for a mistake, while the line has no lot.</summary>
    [HttpPost("{id:guid}/lines/{lineId:guid}/unskip-lanes")]
    [RequirePermission(PermissionFunctions.Harvests, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UnskipLanes(Guid id, Guid lineId, SkipHarvestPlanLanesRequest request, CancellationToken cancellationToken) =>
        (await skips.UnskipAsync(id, lineId, request, cancellationToken)).ToActionResult(Ok);
}
