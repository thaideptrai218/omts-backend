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
    HarvestLotForSaleService forSale, HarvestLaneFinishService laneFinish, HarvestLanesFinishService lanesFinish, HarvestCrewService crews, HarvestLotWeighingService weighing, HarvestLotCageService cages,
    HarvestLotReopeningService reopening, HarvestLotRegisterService register, HarvestLotRegisterFileService registerFiles) : ControllerBase
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
    /// optional <c>estimatedKg</c>, and one <c>photos</c> part per photo (1 to 3). The share of each product is sent as <c>productLines[i].productId</c>, <c>productLines[i].cageCount</c> and
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
        TryDecimal(form.CageCount, out var cageCount);
        var estimatedIsRead = TryDecimal(form.EstimatedKg, out var estimatedKg);
        var (productLines, linesAreUnreadable) = ReadProductLines(form);
        var planLineGiven = !string.IsNullOrWhiteSpace(form.HarvestPlanLineId);
        Guid? planLineId = planLineGiven && Guid.TryParse(form.HarvestPlanLineId!.Trim(), out var lineId) ? lineId : null;
        var request = new RecordHarvestRequest(
            Numbers(form.LaneNumbers), Numbers(form.FinishedLaneNumbers), cageCount, estimatedKg, !estimatedIsRead, Request.Form.Files.HasPartOtherThan(nameof(HarvestLotForm.Photos)),
            form.ConfirmOverPlan, form.OverPlanReason,
            FormNumbers.Crew(form.Crew, form.ReplaceCrew, out var crewUnreadable), crewUnreadable,
            LanePercents(form.LanePercents, out var percentsUnreadable), percentsUnreadable,
            productLines, linesAreUnreadable, planLineId, planLineGiven && planLineId is null);
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
    /// Marks a lane Being harvested as finished without editing a lot (PM decision 11/11/2026): same access as recording a harvest, audited. 200 with <c>changed: false</c> when the lane is already
    /// Harvested; 409 MSG-GEN-09 when it has no harvest lot to finish or the raft has no Open cycle. No body.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/finish-harvest")]
    [ProducesResponseType<LaneHarvestFinished>(StatusCodes.Status200OK)]
    public async Task<IActionResult> FinishLane(Guid raftId, int laneNumber, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] FinishLaneRequest? request, CancellationToken cancellationToken) =>
        (await laneFinish.FinishAsync(raftId, laneNumber, cancellationToken, request is null ? null : new OMTS.Application.Features.Workforce.CompletionApproval(request.OwnerApproved, request.ApprovalReason))).ToActionResult(Ok);

    /// <summary>
    /// Finishes several lanes in one transaction (owner decision 12/11/2026). Body <c>{ laneNumbers: [1..200 distinct], rowVersion? }</c>. 200 lists <c>finished</c> and <c>alreadyFinished</c>;
    /// 409 MSG-GEN-09 with <c>lanes: [{ laneNumber, reasonCode, message }]</c> when any lane cannot be finished (nothing changes); 400 for a bad list.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/finish-harvest")]
    [ProducesResponseType<LanesHarvestFinished>(StatusCodes.Status200OK)]
    public async Task<IActionResult> FinishLanes(Guid raftId, [FromBody] FinishLanesRequest request, CancellationToken cancellationToken) =>
        (await lanesFinish.FinishAsync(raftId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The harvest lot list of the zones the caller can read, any status, with the sums of every match. Query: <c>from</c> and <c>to</c> (harvest date, yyyy-MM-dd), <c>zoneId</c>, <c>raftId</c>, <c>productId</c>,
    /// <c>statusId</c>, <c>weighed</c> (true: weighed, false: waiting for the weighing), <c>closeReason</c> (Unsold, Spoiled, TimeLimitReached, SoldWholeLot), <c>hasPlan</c>, <c>search</c> (lot code or harvest plan code),
    /// <c>page</c>, <c>pageSize</c> (1 to 100) and <c>sort</c> (<c>code</c> by default, <c>harvestedAt</c>, <c>remainingKg</c>) with <c>sortDir</c> (<c>desc</c> by default, or <c>asc</c>).
    /// </summary>
    [HttpGet("api/harvest-lots")]
    [ProducesResponseType<HarvestLotRegisterPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] HarvestLotListQuery query, CancellationToken cancellationToken) =>
        (await register.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The list as filtered and sorted, as .xlsx (at most <see cref="HarvestLotRegisterFileService.MaxRows"/> rows), with the list's zone rule.</summary>
    [HttpGet("api/harvest-lots/export")]
    public async Task<IActionResult> Export([FromQuery] HarvestLotListQuery query, CancellationToken cancellationToken) =>
        (await registerFiles.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

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
        var (productLines, linesAreUnreadable) = ReadProductLines(form);
        var request = new CorrectHarvestLotRequest(
            Numbers(form.LaneNumbers), Numbers(form.FinishedLaneNumbers), cageCount, estimatedKg, form.KeepPhotoKeys, form.Reason, form.RowVersion, !estimatedIsRead,
            Request.Form.Files.HasPartOtherThan(nameof(HarvestLotForm.Photos)), form.ConfirmOverPlan, form.OverPlanReason, FormNumbers.Crew(form.Crew, form.ReplaceCrew, out var crewUnreadable), crewUnreadable,
            LanePercents(form.LanePercents, out var percentsUnreadable), percentsUnreadable, productLines, linesAreUnreadable);
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
            var cagesRead = TryDecimal(line?.CageCount, out var cages);
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

    /// <summary>The product ledger of one lot, oldest first: the harvest at the weighed kg, sales, disposals, the close and the reversals that undid any of them. Harvests View, zone-scoped.</summary>
    [HttpGet("api/harvest-lots/{lotId:guid}/movements")]
    [ProducesResponseType<IReadOnlyList<HarvestLotMovementView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Movements(Guid lotId, CancellationToken cancellationToken) =>
        (await register.MovementsAsync(lotId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The weighing sheet (phiếu cân) of a lot: the cages weighed at the port in order, the sum per product line, the last completed weighing, <c>needsCompleting</c> when the cages changed since and
    /// <c>canEdit</c>. Harvests View, zone-scoped.
    /// </summary>
    [HttpGet("api/harvest-lots/{lotId:guid}/cages")]
    [ProducesResponseType<LotWeighingSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cages(Guid lotId, CancellationToken cancellationToken) =>
        (await cages.SheetAsync(lotId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Weighs one more cage (owner decision of 24/11/2026): <c>kg</c> (above 0, at most 3 decimals), <c>productId</c> (one of the lot's product lines; left out, the product of its only line, as a lot holds one) and an optional
    /// <c>clientOperationId</c> made by the app, so a cage sent again after a lost answer is not weighed twice. The lot keeps its kg until the weighing is completed (<c>weigh</c>). A Zone Manager of the
    /// lot's zone or the Cooperative Owner. 200 with the sheet; 409 MSG-GEN-09 when the lot is Closed.
    /// </summary>
    [HttpPost("api/harvest-lots/{lotId:guid}/cages")]
    [ProducesResponseType<LotWeighingSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddCage(Guid lotId, AddLotCageRequest request, CancellationToken cancellationToken) =>
        (await cages.AddAsync(lotId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Weighs a cage again or puts it under another product of the lot: <c>kg</c> and <c>productId</c> as when it was weighed. Same callers and answers as weighing a cage.</summary>
    [HttpPut("api/harvest-lots/{lotId:guid}/cages/{cageId:guid}")]
    [ProducesResponseType<LotWeighingSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateCage(Guid lotId, Guid cageId, UpdateLotCageRequest request, CancellationToken cancellationToken) =>
        (await cages.UpdateAsync(lotId, cageId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Takes a cage off the sheet; its number is not given again. Same callers and answers as weighing a cage.</summary>
    [HttpDelete("api/harvest-lots/{lotId:guid}/cages/{cageId:guid}")]
    [ProducesResponseType<LotWeighingSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveCage(Guid lotId, Guid cageId, CancellationToken cancellationToken) =>
        (await cages.RemoveAsync(lotId, cageId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Completes the weighing ("Hoàn tất") from the cages weighed: the kg of each product line is the sum of its cages (of the lot, the sum of every cage when it has no lines); <c>weighedAt</c> (default now,
    /// not before the harvest, not in the future) and <c>rowVersion</c> of the sheet. A lot Awaiting weighing becomes Open, for sale; an Open or Reopened lot is weighed again after a cage was corrected. The
    /// Harvest rows of the product ledger are written at the weighed kg (again: the earlier ones undone, nothing else touched) and the figures of the lot count from them. MSG-GEN-01 names <c>Cages</c> (none
    /// weighed), <c>Line {n} cages</c> (a product line with no cage) and <c>Cage {number} product</c> (a cage whose product is no longer on the lot). A Zone Manager of the lot's zone or the Cooperative Owner.
    /// 200 with the lot and MSG-GEN-05; 409 MSG-GEN-09 when the lot is Closed.
    /// </summary>
    [HttpPost("api/harvest-lots/{lotId:guid}/weigh")]
    [ProducesResponseType<HarvestLotChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Weigh(Guid lotId, WeighHarvestLotRequest request, CancellationToken cancellationToken) =>
        (await weighing.WeighAsync(lotId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Reopens a lot closed Unsold, Spoiled, by the time limit or with nothing left, with a <c>reason</c> and <c>rowVersion</c>; the kg the close took off the stock come back. A lot closed by an invoice reopens only
    /// through that invoice (409 MSG-GEN-09). A Zone Manager of the lot's zone or the Cooperative Owner. 200 with the lot and MSG-GEN-05.
    /// </summary>
    [HttpPost("api/harvest-lots/{lotId:guid}/reopen")]
    [ProducesResponseType<HarvestLotChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reopen(Guid lotId, ReopenHarvestLotRequest request, CancellationToken cancellationToken) =>
        (await reopening.ReopenAsync(lotId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The lots for sale in the zones of the caller, the latest harvest first: what a sales invoice chooses from (SALE-05 step 1). With <c>salesOrderId</c> the lots recorded from a harvest plan that serves
    /// that order are <c>suggested</c> and come first; every lot carries <c>harvestPlanCode</c> when it was recorded from a plan.
    /// </summary>
    [HttpGet("api/harvest-lots/for-sale")]
    [ProducesResponseType<HarvestLotsForSale>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForSale([FromQuery] Guid? zoneId, [FromQuery] Guid? salesOrderId, CancellationToken cancellationToken) =>
        (await forSale.ListAsync(zoneId, salesOrderId, cancellationToken)).ToActionResult(Ok);
}
