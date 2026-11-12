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
/// hands over, finishes and cancels a plan; a Zone Manager reads the plans handed over that have a line in its zones (never a Draft) and sees only those lines, records lots from them with <c>POST /api/rafts/{raftId}/harvest-lots</c> (<c>harvestPlanLineId</c>) and leaves lanes out ("Không thu").
/// </summary>
[ApiController]
[Route("api/harvest-plans")]
[Authorize]
public sealed class HarvestPlansController(HarvestPlanService plans, HarvestPlanReadService reads, HarvestPlanLaneSkipService skips) : ControllerBase
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

    /// <summary>The plans Assigned or Harvesting in the zones the caller harvests: what the harvest screen records from. Each carries its lines, lanes and the buttons the caller may press.</summary>
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

    /// <summary>"Kết thúc": finishes a plan being harvested by hand; the lanes still to do stay as they are. 409 MSG-GEN-09 for another status. Body <c>{}</c>.</summary>
    [HttpPost("{id:guid}/finish")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Finish(Guid id, HarvestPlanActionRequest request, CancellationToken cancellationToken) =>
        (await plans.FinishAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Cancels a Draft, or an Assigned plan with no lot, with a reason. 409 MSG-PLAN-04 for a plan that has started (finish it instead).</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.HarvestPlans, PermissionAction.Delete, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestPlanSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelHarvestPlanRequest request, CancellationToken cancellationToken) =>
        (await plans.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>"Không thu": leaves lanes of a line out with a reason (<c>laneNumbers</c>, <c>reason</c>); the Owners are told (MSG-PLAN-06). While the plan is Assigned or Harvesting and the line has no lot (409 MSG-PLAN-03).</summary>
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
