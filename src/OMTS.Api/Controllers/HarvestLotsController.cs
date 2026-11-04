using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Harvest;

namespace OMTS.Api.Controllers;

/// <summary>
/// HARVEST-01 – harvest lots of a raft, for a Zone Manager of the raft's zone and the Cooperative Owner, not for an Accountant (§I.4.4). The routes have no zone: the services check the
/// zone of the raft.
/// </summary>
[ApiController]
[Authorize]
public sealed class HarvestLotsController(
    HarvestLotReadService reads, HarvestLotRecordingService recording, HarvestLotCorrectionService corrections, HarvestLotRemovalService removals, HarvestLotForSaleService forSale) : ControllerBase
{
    private const string PhotosField = "Photos";

    // Ten photos of 10 MB plus the multipart envelope; a larger body answers MSG-GEN-01 on the photos.
    private const long MaxUploadBytes = 105 * 1024 * 1024;

    /// <summary>The Harvest lots screen: the lanes with their state today and the lots of the Open cycle, lots for sale first.</summary>
    [HttpGet("api/rafts/{raftId:guid}/harvest-lots")]
    [ProducesResponseType<HarvestLotsScreen>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Screen(Guid raftId, CancellationToken cancellationToken) =>
        (await reads.ScreenAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Records a harvest trip while the raft has an Open cycle. Multipart: one <c>laneNumbers</c> field per lane, one <c>finishedLaneNumbers</c> field per finished lane, <c>cageCount</c>,
    /// optional <c>estimatedKg</c>, and one <c>photos</c> part per photo (none to 10). A failed upload answers 502 MSG-GEN-03 and saves nothing. Notifies the Cooperative Owner
    /// (MSG-HARVEST-01), and when every seeded lane is now Harvested sends the reminder to close the cycle (MSG-CYCLE-08).
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/harvest-lots")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<HarvestLotSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(Guid raftId, [FromForm] HarvestLotForm form, CancellationToken cancellationToken)
    {
        InvariantFormValues.TryInt(form.CageCount, out var cageCount);
        var estimatedIsRead = InvariantFormValues.TryDecimal(form.EstimatedKg, out var estimatedKg);
        var request = new RecordHarvestRequest(
            Numbers(form.LaneNumbers), Numbers(form.FinishedLaneNumbers), cageCount, estimatedKg, !estimatedIsRead, Request.Form.Files.HasPartOtherThan(nameof(HarvestLotForm.Photos)));
        return (await recording.RecordAsync(raftId, request, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
            .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));
    }

    /// <summary>One lot with its figures and photos: the record a notification opens.</summary>
    [HttpGet("api/harvest-lots/{lotId:guid}")]
    [ProducesResponseType<HarvestLotView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid lotId, CancellationToken cancellationToken) =>
        (await reads.GetAsync(lotId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Corrects a lot while its cycle is Open: the recording Zone Manager or the Cooperative Owner. Multipart: the fields of a record, one <c>keepPhotoKeys</c> field per photo to keep, one <c>photos</c>
    /// part per new photo, <c>reason</c> and <c>rowVersion</c>. Left out, <c>estimatedKg</c> stays, or when the cages changed is worked out with the kg per cage of the lot. Does not tell the Owner again.
    /// </summary>
    [HttpPut("api/harvest-lots/{lotId:guid}")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<HarvestLotSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid lotId, [FromForm] HarvestLotForm form, CancellationToken cancellationToken)
    {
        InvariantFormValues.TryInt(form.CageCount, out var cageCount);
        var estimatedIsRead = InvariantFormValues.TryDecimal(form.EstimatedKg, out var estimatedKg);
        var request = new CorrectHarvestLotRequest(
            Numbers(form.LaneNumbers), Numbers(form.FinishedLaneNumbers), cageCount, estimatedKg, form.KeepPhotoKeys, form.Reason, form.RowVersion, !estimatedIsRead,
            Request.Form.Files.HasPartOtherThan(nameof(HarvestLotForm.Photos)));
        return (await corrections.CorrectAsync(lotId, request, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    /// <summary>Removes a lot and its photos while its cycle is Open and it is on no sales invoice; ask with MSG-GEN-04 first. 204; the audit log keeps it with the reason.</summary>
    [HttpPost("api/harvest-lots/{lotId:guid}/remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid lotId, RemoveHarvestLotRequest request, CancellationToken cancellationToken) =>
        (await removals.RemoveAsync(lotId, request, cancellationToken)).ToActionResult(_ => NoContent());

    /// <summary>The lots for sale in the zones of the caller, the latest harvest first: what a sales invoice chooses from (SALE-05 step 1).</summary>
    [HttpGet("api/harvest-lots/for-sale")]
    [ProducesResponseType<HarvestLotsForSale>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForSale([FromQuery] Guid? zoneId, CancellationToken cancellationToken) =>
        (await forSale.ListAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    /// <summary>How many lots are for sale now, of one zone or of every zone: the open harvest lots of the operations dashboard, for the Owner alone (FARM-06, BR-FARM-10).</summary>
    [HttpGet("api/harvest-lots/for-sale/count")]
    [RequireCooperativeOwner]
    [ProducesResponseType<HarvestLotForSaleCount>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForSaleCount([FromQuery] Guid? zoneId, CancellationToken cancellationToken) =>
        Ok(await forSale.CountAsync(zoneId, cancellationToken));

    /// <summary>The numbers sent; empty when none was; null when one cannot be read, so it is refused, not dropped.</summary>
    private static List<int>? Numbers(string?[]? values)
    {
        var numbers = new List<int>(values?.Length ?? 0);
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value) || !InvariantFormValues.TryInt(value, out var number))
            {
                return null;
            }

            numbers.Add(number!.Value);
        }

        return numbers;
    }
}
