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
public sealed class HarvestLotsController(HarvestLotReadService reads, HarvestLotRecordingService recording) : ControllerBase
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
