using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Nursery;

namespace OMTS.Api.Controllers;

/// <summary>
/// NURSERY – ươm giống (decision of 12/11/2026), for a Zone Manager of the zone and the Cooperative Owner, not for an Accountant; it uses the permission of the cultivation cycles (View to read,
/// Create to start and complete, Edit to change). The routes of a batch have no zone: the services check the zone of the batch. The history is at <c>api/nursery-batches/{id}/history</c>.
/// </summary>
[ApiController]
[Authorize]
public sealed class NurseryBatchesController(
    NurseryReadService reads, NurseryBatchService batches, NurseryBatchEditService edits, NurseryCompletionService completions) : ControllerBase
{
    private const string PhotosField = "Photos";

    // Three photos of 10 MB plus the multipart envelope; a larger body answers MSG-GEN-01 on the photos.
    private const long MaxUploadBytes = 35 * 1024 * 1024;

    /// <summary>The seed lots a nursery in the zone can take, with what is on hand in each. With none: an empty list and MSG-CYCLE-03.</summary>
    [HttpGet("api/nursery-batches/seed-lots")]
    [ProducesResponseType<NurserySeedLotList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedLots([FromQuery] Guid zoneId, CancellationToken cancellationToken) =>
        (await reads.SeedLotsAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The Completed batches that still have seed to hang in an Open cycle, for the seeding screen.</summary>
    [HttpGet("api/nursery-batches/available")]
    [ProducesResponseType<AvailableNurseryList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Available([FromQuery] Guid? zoneId, CancellationToken cancellationToken) =>
        (await reads.AvailableAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/nursery-batches")]
    [ProducesResponseType<NurseryBatchPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] NurseryListQuery query, CancellationToken cancellationToken) =>
        (await reads.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/nursery-batches/{batchId:guid}")]
    [ProducesResponseType<NurseryBatchView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid batchId, CancellationToken cancellationToken) =>
        (await reads.GetAsync(batchId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Starts a batch in the zone. Multipart: <c>seedLotId</c>, <c>quantity</c> (pieces), optional <c>startDate</c> and <c>note</c>, and 1 to 3 <c>photos</c> parts. The pieces leave the stock at once
    /// (409 MSG-INPUT-01 when they are not on hand). A failed upload answers 502 MSG-GEN-03 and saves nothing. Notifies the Cooperative Owner when a Zone Manager starts it.
    /// </summary>
    [HttpPost("api/zones/{zoneId:guid}/nursery-batches")]
    [RequirePermission(PermissionFunctions.CultivationCycles, PermissionAction.Create)]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<NurseryBatchSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Start(Guid zoneId, [FromForm] NurseryBatchForm form, CancellationToken cancellationToken) =>
        (await batches.StartAsync(zoneId, form.ToStart(Request.Form.Files.HasPartOtherThan(PhotosField)), await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
            .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>Edits a Nursing batch: <c>quantity</c>, <c>startDate</c>, <c>note</c>, <c>rowVersion</c>, and the start photos (<c>keepPhotoIds</c>, <c>photos</c>) when either is sent. 409 once it is not Nursing.</summary>
    [HttpPut("api/nursery-batches/{batchId:guid}")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<NurseryBatchSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Edit(Guid batchId, [FromForm] NurseryBatchForm form, CancellationToken cancellationToken) =>
        (await edits.EditAsync(batchId, form.ToEdit(Request.Form.Files.HasPartOtherThan(PhotosField)), await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
            .ToActionResult(Ok);

    /// <summary>Cancels a Nursing batch with a reason; the pieces return to the stock. A Completed batch answers 409 NURSERY_NOT_NURSING.</summary>
    [HttpPost("api/nursery-batches/{batchId:guid}/cancel")]
    [ProducesResponseType<NurseryBatchSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid batchId, CancelNurseryRequest request, CancellationToken cancellationToken) =>
        (await edits.CancelAsync(batchId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Completes a Nursing batch and creates one cycle per raft, in one save. Multipart: <c>survivors</c>, optional <c>completedOn</c>, <c>rowVersion</c>, <c>allocations</c> (a JSON array of
    /// <c>{ raftId, quantity, targetYieldKg? }</c> adding up to the survivors) and 1 to 3 <c>photos</c> parts.
    /// </summary>
    [HttpPost("api/nursery-batches/{batchId:guid}/complete")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<NurseryBatchSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(Guid batchId, [FromForm] NurseryBatchForm form, CancellationToken cancellationToken) =>
        (await completions.CompleteAsync(batchId, form.ToComplete(Request.Form.Files.HasPartOtherThan(PhotosField)), await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
            .ToActionResult(Ok);

    /// <summary>Corrects the survivors of a Completed batch (old to new in the history, with a reason), not below the seed hung.</summary>
    [HttpPost("api/nursery-batches/{batchId:guid}/survivors")]
    [ProducesResponseType<NurseryBatchSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CorrectSurvivors(Guid batchId, CorrectSurvivorsRequest request, CancellationToken cancellationToken) =>
        (await completions.CorrectSurvivorsAsync(batchId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Moves the shares of the survivors between the rafts of a Completed batch (one raft, or many); the total stays the survivors.</summary>
    [HttpPost("api/nursery-batches/{batchId:guid}/allocations")]
    [ProducesResponseType<NurseryBatchSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangeAllocations(Guid batchId, ChangeNurseryAllocationsRequest request, CancellationToken cancellationToken) =>
        (await completions.ChangeAllocationsAsync(batchId, request, cancellationToken)).ToActionResult(Ok);
}
