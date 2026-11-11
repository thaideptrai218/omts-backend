using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Application.Features.Sales.Requests;
using OMTS.Application.Features.Sales.Requests.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// SALE-03 (SCR-SALE-02): the Owner responds; Zone Managers only view, every request, since requests have no zone (§I.4.4 R,
/// PM 03/11). The Accountant has no right here. Before accepting, the Owner or the planner of the work day plan asks staff for
/// the shifts (decision of 31/10/2026); after it, the Owner marks the harvest of the request done (decision of 11/11/2026).
/// </summary>
[ApiController]
[Route("api/purchase-requests")]
[Authorize]
public sealed class PurchaseRequestsController(
    PurchaseRequestReadService reads,
    PurchaseRequestResponseService responses,
    PurchaseHarvestService harvests,
    PurchaseStaffingService staffing) : ControllerBase
{
    /// <summary>Those still to be answered first, then newest first. <c>customerId</c> gives the requests of a profile (SALE-04.1).</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.PurchaseRequests, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<Paged<PurchaseRequestView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(int? statusId, Guid? customerId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await reads.SearchAsync(statusId, customerId, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseRequests, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await reads.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/accept")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Accept(Guid id, AcceptPurchaseRequest request, CancellationToken cancellationToken) =>
        (await responses.AcceptAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/decline")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Decline(Guid id, DeclinePurchaseRequest request, CancellationToken cancellationToken) =>
        (await responses.DeclineAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The Owner marks the harvest of an Accepted request done once it has a lot: no lot is recorded for it after this, and the port
    /// weighing starts only then. Body: <c>rowVersion</c> of the request.
    /// </summary>
    [HttpPost("{id:guid}/harvest-done")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkHarvestDone(Guid id, MarkHarvestDone request, CancellationToken cancellationToken) =>
        (await harvests.MarkDoneAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Clears the mark while the request has no weighing, for a raft still to harvest. Body: <c>rowVersion</c> of the request.</summary>
    [HttpPost("{id:guid}/harvest-done/clear")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearHarvestDone(Guid id, MarkHarvestDone request, CancellationToken cancellationToken) =>
        (await harvests.ClearDoneAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>The staff that can be asked for a shift on <c>day</c>, each with the reason it cannot when it cannot.</summary>
    [HttpGet("staffing-employees")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<StaffingEmployeeOption>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StaffingEmployees(DateOnly day, Guid? purchaseRequestId, CancellationToken cancellationToken) =>
        (await staffing.EmployeesAsync(day, cancellationToken, purchaseRequestId)).ToActionResult(Ok);

    [HttpGet("{id:guid}/staffing")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<PurchaseShiftInvitationView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Staffing(Guid id, CancellationToken cancellationToken) =>
        (await staffing.InvitationsAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Sends the staffing plan and asks the chosen staff for the shift; the request is then Awaiting staff.</summary>
    [HttpPost("{id:guid}/staffing")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseStaffingView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PlanStaffing(Guid id, PlanPurchaseStaffing request, CancellationToken cancellationToken) =>
        (await staffing.PlanAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
