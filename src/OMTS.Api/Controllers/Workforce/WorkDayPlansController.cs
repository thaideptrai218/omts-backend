using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.Workforce.Dtos;

namespace OMTS.Api.Controllers.Workforce;

[ApiController]
[Authorize]
[Route("api/work-plans")]
public sealed class WorkDayPlansController(WorkDayPlanService plans) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    public async Task<IActionResult> List(DateOnly from, DateOnly to, CancellationToken ct) => (await plans.ListAsync(from, to, ct)).ToActionResult(Ok);
    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => (await plans.GetAsync(id, ct)).ToActionResult(Ok);
    [HttpGet("purchase/{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    public async Task<IActionResult> Purchase(Guid id, CancellationToken ct) => (await plans.ForPurchaseAsync(id, ct)).ToActionResult(value => new JsonResult(value));
    [HttpGet("responsibles")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Responsibles(Guid raftId, CancellationToken ct) => (await plans.ResponsiblesAsync(raftId, ct)).ToActionResult(Ok);
    [HttpPost]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Create(SaveWorkDayPlan input, CancellationToken ct) => (await plans.SaveAsync(null, input, ct)).ToActionResult(Ok);
    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Update(Guid id, SaveWorkDayPlan input, CancellationToken ct) => (await plans.SaveAsync(id, input, ct)).ToActionResult(Ok);
    [HttpPost("{id:guid}/response")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    public async Task<IActionResult> Respond(Guid id, RespondWorkDayPlan input, CancellationToken ct) => (await plans.RespondAsync(id, input, ct)).ToActionResult(Ok);
    [HttpGet("{id:guid}/employees")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    public async Task<IActionResult> Employees(Guid id, CancellationToken ct) => (await plans.EmployeesAsync(id, ct)).ToActionResult(Ok);
    [HttpPost("{id:guid}/assignments")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    public async Task<IActionResult> Assign(Guid id, AssignWorkDayPlan input, CancellationToken ct) => (await plans.AssignAsync(id, input, ct)).ToActionResult(Ok);
}
