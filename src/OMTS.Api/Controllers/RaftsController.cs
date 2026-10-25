using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Farming.Zones;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-04 – rafts with their lane layout. Writes are Cooperative Owner only; lists, detail and the lane
/// condition view are open to staff holding a grant in the raft's zone.
/// </summary>
[ApiController]
[Authorize]
public sealed class RaftsController(RaftService rafts, RaftConditionService conditions) : ControllerBase
{
    [HttpGet("api/zones/{zoneId:guid}/rafts")]
    [ProducesResponseType<IReadOnlyList<RaftListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid zoneId, int? statusId, CancellationToken cancellationToken) =>
        (await rafts.ListAsync(zoneId, statusId, cancellationToken)).ToActionResult(Ok);

    [HttpPost("api/zones/{zoneId:guid}/rafts")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(Guid zoneId, SaveRaftRequest request, CancellationToken cancellationToken) =>
        (await rafts.CreateAsync(zoneId, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    [HttpGet("api/rafts/{raftId:guid}")]
    [ProducesResponseType<RaftView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid raftId, CancellationToken cancellationToken) =>
        (await rafts.GetAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>A different lane layout creates a new layout version; blocked with MSG11 while a cycle is open.</summary>
    [HttpPut("api/rafts/{raftId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid raftId, SaveRaftRequest request, CancellationToken cancellationToken) =>
        (await rafts.UpdateAsync(raftId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart field <c>file</c>: JPEG or PNG, at most 10 MB.</summary>
    [HttpPost("api/rafts/{raftId:guid}/photo")]
    [RequireCooperativeOwner]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [UploadField("Photo")]
    [ProducesResponseType<RaftView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadPhoto(Guid raftId, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return AppError.Invalid("Photo").ToProblem();
        }

        return (await rafts.UploadPhotoAsync(raftId, await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    [HttpPost("api/rafts/{raftId:guid}/discontinue")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid raftId, ChangeStatusRequest request, CancellationToken cancellationToken) =>
        (await rafts.DiscontinueAsync(raftId, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("api/rafts/{raftId:guid}/reactivate")]
    [RequireCooperativeOwner]
    [ProducesResponseType<RaftView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid raftId, ChangeStatusRequest request, CancellationToken cancellationToken) =>
        (await rafts.ReactivateAsync(raftId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Lane portions as of <c>date</c> (default today) or of <c>cycleId</c>.</summary>
    [HttpGet("api/rafts/{raftId:guid}/condition")]
    [ProducesResponseType<RaftConditionView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Condition(Guid raftId, DateOnly? date, Guid? cycleId, CancellationToken cancellationToken) =>
        (await conditions.GetAsync(raftId, date, cycleId, cancellationToken)).ToActionResult(Ok);
}
