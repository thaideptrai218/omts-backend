using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Application.Features.WorkTracking;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Enums;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;

namespace OMTS.Api.Controllers;

/// <summary>
/// LAB-03 work assignment and LAB-05 piecework completion in one zone; Cooperative Owner everywhere, staff only in zones where
/// a grant gives WORK_ASSIGNMENTS (View: list/detail/picker, Create: assign, Edit: edit/confirm completion, Delete: cancel).
/// Dates in a month whose payroll is finalized answer 409 PERIOD_LOCKED.
/// </summary>
[ApiController]
[Route("api/zones/{zoneId:guid}/work-assignments")]
[Authorize]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
public sealed class WorkAssignmentsController(
    WorkAssignmentService assignments,
    WorkAssignmentQueryService queries,
    PieceworkCompletionService completions,
    LaborPickerService pickers) : ControllerBase
{
    // Five 5 MB photos plus the multipart envelope.
    private const long MaxEvidenceBytes = 27L * 1024 * 1024;

    /// <summary>
    /// <c>from</c>/<c>to</c> (default: this month, at most 366 days), <c>employeeId</c>/<c>workerId</c>/<c>teamId</c>,
    /// <c>paymentType</c> (DailyWage, Piecework), <c>state</c> (active, cancelled, completed).
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.WorkAssignments, View)]
    [ProducesResponseType<Paged<WorkAssignmentView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        Guid zoneId, DateOnly? from, DateOnly? to, Guid? employeeId, Guid? workerId, Guid? teamId, WorkPaymentType? paymentType, string? state,
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        (await queries.SearchAsync(zoneId, from, to, employeeId, workerId, teamId, paymentType, state, page, pageSize, cancellationToken)).ToActionResult(Ok);

    /// <summary>Assignee picker: Employed employees working in the zone, In Use workers and teams (id, name, team, status only).</summary>
    [HttpGet("assignees")]
    [RequirePermission(PermissionFunctions.WorkAssignments, View)]
    [ProducesResponseType<IReadOnlyList<LaborPickerItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Assignees(Guid zoneId, string? search, CancellationToken cancellationToken) =>
        Ok(await pickers.ListAsync(zoneId, search, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, View)]
    [ProducesResponseType<WorkAssignmentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid zoneId, Guid id, CancellationToken cancellationToken) =>
        (await queries.GetAsync(zoneId, id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Example: <c>{"workDate":"2026-10-24","workerId":"…","task":"Vệ sinh lồng","paymentType":"Piecework","pieceUnit":"rope",
    /// "plannedQuantity":40,"pieceRate":2500}</c>. A person already assigned that day answers 409 MSG49 with
    /// <c>requiresConfirmation: true</c>; resend with <c>confirmDuplicate: true</c> to save.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create)]
    [ProducesResponseType<WorkAssignmentView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(Guid zoneId, SaveWorkAssignmentRequest request, CancellationToken cancellationToken) =>
        (await assignments.CreateAsync(zoneId, request, cancellationToken)).ToActionResult(a => StatusCode(StatusCodes.Status201Created, a));

    /// <summary>Editable until the month is finalized; after completion is confirmed only Task and Notes can change.</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, Edit)]
    [ProducesResponseType<WorkAssignmentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid zoneId, Guid id, SaveWorkAssignmentRequest request, CancellationToken cancellationToken) =>
        (await assignments.UpdateAsync(zoneId, id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Cancel instead of delete: reason required; refused after completion is confirmed (MSG61).</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.WorkAssignments, Delete)]
    [ProducesResponseType<WorkAssignmentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid zoneId, Guid id, CancelWorkAssignmentRequest request, CancellationToken cancellationToken) =>
        (await assignments.CancelAsync(zoneId, id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// LAB-05: example <c>{"completedQuantity":37,"completionDate":"2026-10-22"}</c>; the response carries
    /// <c>completion.pieceworkAmount</c> (37 × 2,500 = 92,500 → 93,000). Re-confirming needs a <c>reason</c>.
    /// </summary>
    [HttpPost("{id:guid}/completion")]
    [RequirePermission(PermissionFunctions.WorkAssignments, Edit)]
    [ProducesResponseType<WorkAssignmentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfirmCompletion(Guid zoneId, Guid id, ConfirmCompletionRequest request, CancellationToken cancellationToken) =>
        (await completions.ConfirmAsync(zoneId, id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart <c>files</c>: completion evidence photos (JPEG/PNG, at most 5 MB each, 5 in all).</summary>
    [HttpPost("{id:guid}/completion/evidence")]
    [RequirePermission(PermissionFunctions.WorkAssignments, Edit)]
    [RequestSizeLimit(MaxEvidenceBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<WorkAssignmentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddCompletionEvidence(Guid zoneId, Guid id, [FromForm] List<IFormFile>? files, CancellationToken cancellationToken) =>
        (await completions.AddEvidenceAsync(zoneId, id, await files.ToIncomingFilesAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
}
