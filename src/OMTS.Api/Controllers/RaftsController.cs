using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Farming.Zones;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-04 – rafts with their lanes and photos. Writes are Cooperative Owner only; lists, detail and the lanes of today
/// are open to staff holding a grant in the raft's zone.
/// </summary>
[ApiController]
[Authorize]
public sealed class RaftsController(RaftReadService reads, RaftService rafts, RaftPhotoService photos, RaftStatusService statuses) : ControllerBase
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

    [HttpGet("api/rafts/{raftId:guid}")]
    [ProducesResponseType<RaftView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid raftId, CancellationToken cancellationToken) =>
        (await reads.GetAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The lanes of the raft today with their status In Use or Removed; CYCLE adds the derived lane state.</summary>
    [HttpGet("api/rafts/{raftId:guid}/lanes/today")]
    [ProducesResponseType<RaftLanesToday>(StatusCodes.Status200OK)]
    public async Task<IActionResult> LanesToday(Guid raftId, CancellationToken cancellationToken) =>
        (await reads.LanesTodayAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>A Discontinued raft answers MSG-GEN-09; a new number of lanes answers MSG-GEN-06 while a cycle is open.</summary>
    [HttpPut("api/rafts/{raftId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid raftId, SaveRaftRequest request, CancellationToken cancellationToken) =>
        (await rafts.UpdateAsync(raftId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Adds one photo, at most three per raft. Multipart field <c>file</c>: JPEG or PNG, at most 10 MB.</summary>
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
