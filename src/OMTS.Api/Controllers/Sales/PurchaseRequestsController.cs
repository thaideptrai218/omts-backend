using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Sales.Requests.Dtos;
using OMTS.Application.Features.Sales.Requests;
using OMTS.Application.Features.Sales;

namespace OMTS.Api.Controllers.Sales;

[ApiController]
[Authorize]
[Route("api/purchase-requests")]
public sealed class PurchaseRequestsController(PurchaseRequestService requests, PurchaseStaffingService staffing) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.PurchaseRequests, PermissionAction.View, PermissionScope.AnyZone)]
    public async Task<IActionResult> List(string? status = null, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        (await requests.ListAsync(false, status, page, pageSize, ct)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseRequests, PermissionAction.View, PermissionScope.AnyZone)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        (await requests.GetAsync(id, false, ct)).ToActionResult(Ok);

    [HttpPost("{id:guid}/response")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Respond(Guid id, RespondPurchaseRequest input, CancellationToken ct) =>
        (await requests.RespondAsync(id, input, ct)).ToActionResult(Ok);

    [HttpGet("staffing-employees")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Employees(DateOnly day, CancellationToken ct) =>
        (await staffing.EmployeesAsync(day, ct)).ToActionResult(Ok);

    [HttpGet("{id:guid}/staffing")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Staffing(Guid id, CancellationToken ct) =>
        (await staffing.InvitationsAsync(id, ct)).ToActionResult(Ok);

    [HttpPost("{id:guid}/staffing")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Plan(Guid id, PlanPurchaseStaffing input, CancellationToken ct) =>
        (await staffing.PlanAsync(id, input, ct)).ToActionResult(Ok);
}
