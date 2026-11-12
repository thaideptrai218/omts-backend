using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Harvest;
using static OMTS.Api.Common.FormNumbers;

namespace OMTS.Api.Controllers;

/// <summary>
/// HARVEST-01 – harvest lots of a raft, for a Zone Manager of the raft's zone and the Cooperative Owner, not for an Accountant (§I.4.4). The routes have no zone: the services check the
/// zone of the raft.
/// </summary>
[ApiController]
[Authorize]
public sealed class HarvestLotsController(
    HarvestLotReadService reads, HarvestLotRecordingService recording, HarvestLotCorrectionService corrections, HarvestLotRemovalService removals, HarvestLotClosingService closing,
    HarvestLotForSaleService forSale, HarvestLaneFinishService laneFinish, HarvestLanesFinishService lanesFinish, HarvestCrewService crews) : ControllerBase
{
    private const string PhotosField = "Photos";

    // Three photos of 10 MB plus the multipart envelope (owner decision of 12/11/2026); a larger body answers MSG-GEN-01 on the photos.
    private const long MaxUploadBytes = 35 * 1024 * 1024;

    /// <summary>The Harvest lots screen: the lanes with their state today and the lots of the Open cycle, lots for sale first.</summary>
    [HttpGet("api/rafts/{raftId:guid}/harvest-lots")]
    [ProducesResponseType<HarvestLotsScreen>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Screen(Guid raftId, CancellationToken cancellationToken) =>
        (await reads.ScreenAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Records a harvest trip while the raft has an Open cycle. Multipart: one <c>laneNumbers</c> field per lane, one <c>finishedLaneNumbers</c> field per finished lane, <c>cageCount</c>,
    /// optional <c>estimatedKg</c>, <c>purchaseRequestId</c> of an Accepted request whose harvest is not marked done, and one <c>photos</c> part per photo (none to 10).
    /// A failed upload answers 502 MSG-GEN-03 and saves nothing. Notifies the Cooperative Owner
    /// (MSG-HARVEST-01), and when every seeded lane is now Harvested sends the reminder to close the cycle (MSG-CYCLE-08).
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/harvest-lots")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<HarvestLotSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(Guid raftId, [FromForm] HarvestLotForm form, CancellationToken cancellationToken)
    {
        TryDecimal(form.CageCount, out var cageCount);
        var estimatedIsRead = TryDecimal(form.EstimatedKg, out var estimatedKg);
        var request = new RecordHarvestRequest(
            Numbers(form.LaneNumbers), Numbers(form.FinishedLaneNumbers), cageCount, estimatedKg, !estimatedIsRead, Request.Form.Files.HasPartOtherThan(nameof(HarvestLotForm.Photos)),
            form.ConfirmOverPlan, form.OverPlanReason,
            Guid.TryParse(form.PurchaseRequestId, out var requestId) && requestId != Guid.Empty ? requestId : null,
            ReadCrew(form.Crew, form.ReplaceCrew, out var crewUnreadable), crewUnreadable,
            LanePercents(form.LanePercents, out var percentsUnreadable), percentsUnreadable);
        return (await recording.RecordAsync(raftId, request, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
            .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));
    }

    /// <summary>
    /// The employees assigned to the raft on a day (<c>date</c>, default today) as the suggested crew of a harvest there: read only, from the work assignments. Any other employee can still be chosen.
    /// </summary>
    [HttpGet("api/rafts/{raftId:guid}/crew-suggestions")]
    [ProducesResponseType<HarvestCrewSuggestions>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CrewSuggestions(Guid raftId, [FromQuery] DateOnly? date, CancellationToken cancellationToken) =>
        (await crews.SuggestAsync(raftId, date, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The crew of the form: one <c>crew</c> field per employee id. Null when no field was sent (a correction then leaves the crew as it is) unless <c>replaceCrew</c> is true, which makes it an empty crew. An id that is not
    /// a GUID is reported as unreadable, so it is refused instead of dropped.
    /// </summary>
    private static IReadOnlyList<Guid>? ReadCrew(string?[]? values, bool replace, out bool unreadable)
    {
        unreadable = false;
        if (values is null)
        {
            return replace ? [] : null;
        }

        var ids = new List<Guid>(values.Length);
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (Guid.TryParse(value, out var id) && id != Guid.Empty)
            {
                ids.Add(id);
            }
            else
            {
                unreadable = true;
            }
        }

        return ids;
    }

    /// <summary>
    /// Marks a lane Being harvested as finished without editing a lot (PM decision 11/11/2026): same access as recording a harvest, audited. 200 with <c>changed: false</c> when the lane is already
    /// Harvested; 409 MSG-GEN-09 when it has no harvest lot to finish or the raft has no Open cycle. No body.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/finish-harvest")]
    [ProducesResponseType<LaneHarvestFinished>(StatusCodes.Status200OK)]
    public async Task<IActionResult> FinishLane(Guid raftId, int laneNumber, CancellationToken cancellationToken) =>
        (await laneFinish.FinishAsync(raftId, laneNumber, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Finishes several lanes in one transaction (owner decision 12/11/2026). Body <c>{ laneNumbers: [1..200 distinct], rowVersion? }</c>. 200 lists <c>finished</c> and <c>alreadyFinished</c>;
    /// 409 MSG-GEN-09 with <c>lanes: [{ laneNumber, reasonCode, message }]</c> when any lane cannot be finished (nothing changes); 400 for a bad list.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/finish-harvest")]
    [ProducesResponseType<LanesHarvestFinished>(StatusCodes.Status200OK)]
    public async Task<IActionResult> FinishLanes(Guid raftId, [FromBody] FinishLanesRequest request, CancellationToken cancellationToken) =>
        (await lanesFinish.FinishAsync(raftId, request, cancellationToken)).ToActionResult(Ok);

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
        TryDecimal(form.CageCount, out var cageCount);
        var estimatedIsRead = TryDecimal(form.EstimatedKg, out var estimatedKg);
        var request = new CorrectHarvestLotRequest(
            Numbers(form.LaneNumbers), Numbers(form.FinishedLaneNumbers), cageCount, estimatedKg, form.KeepPhotoKeys, form.Reason, form.RowVersion, !estimatedIsRead,
            Request.Form.Files.HasPartOtherThan(nameof(HarvestLotForm.Photos)), form.ConfirmOverPlan, form.OverPlanReason, ReadCrew(form.Crew, form.ReplaceCrew, out var crewUnreadable), crewUnreadable,
            LanePercents(form.LanePercents, out var percentsUnreadable), percentsUnreadable);
        return (await corrections.CorrectAsync(lotId, request, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    /// <summary>Removes a lot and its photos while its cycle is Open and it is on no sales invoice; ask with MSG-GEN-04 first. 204; the audit log keeps it with the reason.</summary>
    [HttpPost("api/harvest-lots/{lotId:guid}/remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid lotId, RemoveHarvestLotRequest request, CancellationToken cancellationToken) =>
        (await removals.RemoveAsync(lotId, request, cancellationToken)).ToActionResult(_ => NoContent());

    /// <summary>
    /// HARVEST-02: closes a lot still for sale (Open or Reopened) with <c>leftoverKg</c> (0 or more; propose the lot's remaining kg) and, when it is above 0, <c>reason</c> <c>Unsold</c> or
    /// <c>Spoiled</c>, plus <c>rowVersion</c>; ask with MSG-GEN-04 first. A Zone Manager of the lot's zone or the Cooperative Owner, also after the cycle is closed. 200 with the lot and MSG-GEN-05;
    /// 409 MSG-GEN-09 when it is no longer for sale.
    /// </summary>
    [HttpPost("api/harvest-lots/{lotId:guid}/close")]
    [ProducesResponseType<HarvestLotClosed>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Close(Guid lotId, CloseHarvestLotRequest request, CancellationToken cancellationToken) =>
        (await closing.CloseAsync(lotId, request, cancellationToken)).ToActionResult(Ok);

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
}
