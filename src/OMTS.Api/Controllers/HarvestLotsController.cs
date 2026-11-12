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
    HarvestLotForSaleService forSale) : ControllerBase
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
    /// optional <c>estimatedKg</c>, and one <c>photos</c> part per photo (none to 10). The share of each product is sent as <c>productLines[i].productId</c>, <c>productLines[i].cageCount</c> and
    /// optional <c>productLines[i].kg</c> (i from 0, no gap): the lot's cages and kg are then the sums of the lines (<c>cageCount</c> and <c>estimatedKg</c> are ignored) and each line adds its kg to the
    /// stock of its product. <c>harvestPlanLineId</c> records the lot from a line of a harvest plan (KHT): the lanes are then those of the line without the lanes left out (<c>laneNumbers</c> is ignored), the lot
    /// carries the plan, and the plan moves on (409 MSG-PLAN-03 when the line already has its lot). A failed upload answers 502 MSG-GEN-03 and saves nothing. Notifies the Cooperative Owner
    /// (MSG-HARVEST-01), and when every seeded lane is now Harvested sends the reminder to close the cycle (MSG-CYCLE-08).
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/harvest-lots")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<HarvestLotSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(Guid raftId, [FromForm] HarvestLotForm form, CancellationToken cancellationToken)
    {
        TryInt(form.CageCount, out var cageCount);
        var estimatedIsRead = TryDecimal(form.EstimatedKg, out var estimatedKg);
        var (productLines, linesAreUnreadable) = ReadProductLines(form);
        var planLineGiven = !string.IsNullOrWhiteSpace(form.HarvestPlanLineId);
        Guid? planLineId = planLineGiven && Guid.TryParse(form.HarvestPlanLineId!.Trim(), out var lineId) ? lineId : null;
        var request = new RecordHarvestRequest(
            Numbers(form.LaneNumbers), Numbers(form.FinishedLaneNumbers), cageCount, estimatedKg, !estimatedIsRead, Request.Form.Files.HasPartOtherThan(nameof(HarvestLotForm.Photos)),
            productLines, linesAreUnreadable, planLineId, planLineGiven && planLineId is null);
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
        TryInt(form.CageCount, out var cageCount);
        var estimatedIsRead = TryDecimal(form.EstimatedKg, out var estimatedKg);
        var (productLines, linesAreUnreadable) = ReadProductLines(form);
        var request = new CorrectHarvestLotRequest(
            Numbers(form.LaneNumbers), Numbers(form.FinishedLaneNumbers), cageCount, estimatedKg, form.KeepPhotoKeys, form.Reason, form.RowVersion, !estimatedIsRead,
            Request.Form.Files.HasPartOtherThan(nameof(HarvestLotForm.Photos)), productLines, linesAreUnreadable);
        return (await corrections.CorrectAsync(lotId, request, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    /// <summary>The product lines of the form; null when it sent none. A value that cannot be read marks the lines unreadable, so the request is refused, not dropped.</summary>
    private (List<HarvestProductLineInput>? Lines, bool Unreadable) ReadProductLines(HarvestLotForm form)
    {
        // MVC binds the indexes 0, 1, 2… and stops at the first gap: a later index would be dropped without a word, so it makes the lines unreadable.
        var posted = Request.HasFormContentType
            ? Request.Form.Keys.Select(k => System.Text.RegularExpressions.Regex.Match(k, @"^productLines\[(\d+)\]\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList()
            : [];
        if (form.ProductLines is not { Count: > 0 } sent)
        {
            return (null, posted.Count > 0);
        }

        var lines = new List<HarvestProductLineInput>(sent.Count);
        var unreadable = false;
        foreach (var line in sent)
        {
            Guid? productId = Guid.TryParse(line?.ProductId?.Trim(), out var id) ? id : null;
            var cagesRead = TryInt(line?.CageCount, out var cages);
            var kgRead = TryDecimal(line?.Kg, out var kg);
            unreadable |= !cagesRead || !kgRead || (line?.ProductId is { Length: > 0 } && productId is null);
            lines.Add(new HarvestProductLineInput(productId, cages, kg));
        }

        return (lines, unreadable || posted.Count != sent.Count);
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

    /// <summary>
    /// The lots for sale in the zones of the caller, the latest harvest first: what a sales invoice chooses from (SALE-05 step 1). With <c>salesOrderId</c> the lots recorded from a harvest plan that serves
    /// that order are <c>suggested</c> and come first; every lot carries <c>harvestPlanCode</c> when it was recorded from a plan.
    /// </summary>
    [HttpGet("api/harvest-lots/for-sale")]
    [ProducesResponseType<HarvestLotsForSale>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForSale([FromQuery] Guid? zoneId, [FromQuery] Guid? salesOrderId, CancellationToken cancellationToken) =>
        (await forSale.ListAsync(zoneId, salesOrderId, cancellationToken)).ToActionResult(Ok);

    /// <summary>How many lots are for sale now, of one zone or of every zone: the open harvest lots of the operations dashboard, for the Owner alone (FARM-06, BR-FARM-10).</summary>
    [HttpGet("api/harvest-lots/for-sale/count")]
    [RequireCooperativeOwner]
    [ProducesResponseType<HarvestLotForSaleCount>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForSaleCount([FromQuery] Guid? zoneId, CancellationToken cancellationToken) =>
        Ok(await forSale.CountAsync(zoneId, cancellationToken));
}
