using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Farming.Rafts.FormScan;
using OMTS.Application.Features.Scans;
using OMTS.Application.Features.Farming.Zones;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-04 – rafts with their lanes and photos. Writes are Cooperative Owner only; lists, detail and the lanes of today
/// are open to the Owner and to a Zone Manager in the raft's zone, not to an Accountant (§I.4.4).
/// </summary>
[ApiController]
[Authorize]
public sealed class RaftsController(RaftReadService reads, RaftService rafts, RaftPhotoService photos, RaftStatusService statuses, RaftFormScanService formScans) : ControllerBase
{
    [HttpGet("api/zones/{zoneId:guid}/rafts")]
    [ProducesResponseType<IReadOnlyList<RaftListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid zoneId, int? statusId, CancellationToken cancellationToken) =>
        (await reads.ListAsync(zoneId, statusId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The proposed code of a new raft of the zone ({zone code}-B01, -B02…; GBR-03); the Owner may type another one.</summary>
    [HttpGet("api/zones/{zoneId:guid}/rafts/next-code")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftCodeProposal>(StatusCodes.Status200OK)]
    public async Task<IActionResult> NextCode(Guid zoneId, CancellationToken cancellationToken) =>
        (await reads.ProposeCodeAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    [HttpPost("api/zones/{zoneId:guid}/rafts")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(Guid zoneId, SaveRaftRequest request, CancellationToken cancellationToken) =>
        (await rafts.CreateAsync(zoneId, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>
    /// Starts reading a photographed raft form (multipart <c>photo</c>, JPEG/PNG, at most 10 MB) into suggestions for the create-raft
    /// screen; same permission as creating a raft. 202 with the <c>scanId</c> to poll. A full queue is 503 OCR_BUSY, more than 30
    /// scans an hour per account is 429. Nothing is saved.
    /// </summary>
    [HttpPost("api/rafts/form-scans")]
    [RequireCooperativeOwner]
    [EnableRateLimiting(RateLimitingExtensions.RaftFormScans)]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [UploadField(PhotoScanGateway.PhotoField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ScanStarted>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> StartFormScan(IFormFile? photo, CancellationToken cancellationToken)
    {
        var file = photo is null ? null : await photo.ToIncomingFileAsync(cancellationToken);
        return formScans.Start(file).ToActionResult(started => Accepted(started));
    }

    /// <summary>The form scan: <c>status</c>, <c>errorCode</c> and, when Done, the <c>reading</c> (fields copied as written). Kept 15 minutes; 404 for another account.</summary>
    [HttpGet("api/rafts/form-scans/{scanId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ScanStatusView<RaftFormReading>>(StatusCodes.Status200OK)]
    public IActionResult GetFormScan(Guid scanId) => formScans.Get(scanId).ToActionResult(Ok);

    [HttpGet("api/rafts/{raftId:guid}")]
    [ProducesResponseType<RaftView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid raftId, CancellationToken cancellationToken) =>
        (await reads.GetAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>A Discontinued raft answers MSG-GEN-09; a new number of lanes answers MSG-GEN-06 while a cycle is open.</summary>
    [HttpPut("api/rafts/{raftId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid raftId, SaveRaftRequest request, CancellationToken cancellationToken) =>
        (await rafts.UpdateAsync(raftId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Adds one photo, at most three per raft. Multipart field <c>file</c>, once: JPEG or PNG, at most 10 MB.</summary>
    [HttpPost("api/rafts/{raftId:guid}/photos")]
    [RequireCooperativeOwner]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [UploadField("Photo")]
    [ProducesResponseType<RaftSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddPhoto(Guid raftId, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return AppError.Invalid("Photo").ToProblem();
        }

        // The binder keeps the first of several parts named "file": refused, so no photo is dropped without a word.
        if (Request.Form.Files.GetFiles(nameof(file)).Count > 1)
        {
            return AppError.Invalid("Photo", "Send one photo per request.").ToProblem();
        }

        return (await photos.AddAsync(raftId, await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    [HttpDelete("api/rafts/{raftId:guid}/photos/{photoId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemovePhoto(Guid raftId, Guid photoId, CancellationToken cancellationToken) =>
        (await photos.RemoveAsync(raftId, photoId, cancellationToken)).ToActionResult(Ok);

    [HttpPost("api/rafts/{raftId:guid}/discontinue")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid raftId, ChangeStatusRequest request, CancellationToken cancellationToken) =>
        (await statuses.DiscontinueAsync(raftId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Resume (FARM-04.4). <c>/reactivate</c> is the older route of the same action, kept for one release.</summary>
    [HttpPost("api/rafts/{raftId:guid}/resume")]
    [HttpPost("api/rafts/{raftId:guid}/reactivate")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid raftId, ChangeStatusRequest request, CancellationToken cancellationToken) =>
        (await statuses.ReactivateAsync(raftId, request, cancellationToken)).ToActionResult(Ok);
}
