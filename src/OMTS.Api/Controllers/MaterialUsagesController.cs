using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Cultivation.MaterialUsages;

namespace OMTS.Api.Controllers;

/// <summary>
/// CYCLE-03 – material usage of a raft, for a Zone Manager of the raft's zone and the Cooperative Owner, not for an Accountant (§I.4.4). The routes have no zone: the services check the
/// zone of the raft. The items to choose from are <c>GET api/farming-input-items</c> with the group of materials and the status In Use.
/// </summary>
[ApiController]
[Authorize]
public sealed class MaterialUsagesController(MaterialUsageReadService reads, MaterialUsageService usages, MaterialUsageEditService edits) : ControllerBase
{
    /// <summary>The usage of the raft by cycle and by repair, with the dates when the raft had no cycle in a separate group.</summary>
    [HttpGet("api/rafts/{raftId:guid}/material-usages")]
    [ProducesResponseType<MaterialUsageList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid raftId, CancellationToken cancellationToken) =>
        (await reads.ListAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Records one repair: one record per item. A date in a closed cycle answers 409 MSG-GEN-09 naming the cycle.</summary>
    [HttpPost("api/rafts/{raftId:guid}/material-usages")]
    [ProducesResponseType<MaterialUsageRecorded>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(Guid raftId, RecordMaterialUsageRequest request, CancellationToken cancellationToken) =>
        (await usages.RecordAsync(raftId, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    [HttpGet("api/material-usages/{usageId:guid}")]
    [ProducesResponseType<MaterialUsageView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid usageId, CancellationToken cancellationToken) =>
        (await reads.GetAsync(usageId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Edits a record that no closed cycle locks, with the reason for the edit; another raft In Use moves it there.</summary>
    [HttpPut("api/material-usages/{usageId:guid}")]
    [ProducesResponseType<MaterialUsageSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Edit(Guid usageId, EditMaterialUsageRequest request, CancellationToken cancellationToken) =>
        (await edits.EditAsync(usageId, request, cancellationToken)).ToActionResult(Ok);
}
