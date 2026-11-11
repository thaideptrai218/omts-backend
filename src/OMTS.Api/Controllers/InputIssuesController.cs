using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Issues;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>
/// The issues of input items: uses in a zone (XD) and disposals (XH). Every action is checked by the service, which knows the kind and the zone of the issue: the Owner does everything; a
/// Zone Manager records, completes, edits and cancels the use issues of its zones; the Accountant reads and exports every issue; disposals are recorded by the Owner only.
/// </summary>
[ApiController]
[Route("api/input-issues")]
[Authorize]
public sealed class InputIssuesController(InputIssueService issues) : ControllerBase
{
    /// <summary>The issues the caller may see, with filters, sort, paging and the total value of the Completed ones.</summary>
    [HttpGet]
    [ProducesResponseType<InputIssuePage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] InputIssueListQuery query, CancellationToken cancellationToken) =>
        (await issues.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] InputIssueListQuery query, CancellationToken cancellationToken) =>
        (await issues.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>The code the next issue of the kind would get (XD000001 for Use, XH000001 for Disposal); not reserved.</summary>
    [HttpGet("next-code")]
    [ProducesResponseType<InputIssueNextCode>(StatusCodes.Status200OK)]
    public async Task<IActionResult> NextCode(InputIssueKind kind, CancellationToken cancellationToken) =>
        (await issues.NextCodeAsync(Enum.IsDefined(kind) ? kind : InputIssueKind.Use, cancellationToken)).ToActionResult(Ok);

    /// <summary>The employees who work in the zone: who may be named as the receiver of a use.</summary>
    [HttpGet("receivers")]
    [RequirePermission(PermissionFunctions.InputIssues, PermissionAction.View)]
    [ProducesResponseType<IReadOnlyList<InputIssueReceiverView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Receivers(Guid zoneId, CancellationToken cancellationToken) =>
        (await issues.ReceiversAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<InputIssueView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await issues.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Saves a Draft or, with <c>complete</c>, records the issue at once. 409 MSG-INPUT-01 when completing would take an item or seed lot below zero and the cooperative does not allow it.</summary>
    [HttpPost]
    [ProducesResponseType<InputIssueView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveInputIssueRequest request, CancellationToken cancellationToken) =>
        (await issues.CreateAsync(request, cancellationToken)).ToActionResult(i => StatusCode(StatusCodes.Status201Created, i));

    /// <summary>Replaces a Draft or a Completed issue (the row version is required; a Completed issue writes its quantities again).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<InputIssueView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveInputIssueRequest request, CancellationToken cancellationToken) =>
        (await issues.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<InputIssueView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(Guid id, CompleteInputIssueRequest request, CancellationToken cancellationToken) =>
        (await issues.CompleteAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<InputIssueView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelInputIssueRequest request, CancellationToken cancellationToken) =>
        (await issues.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Removes a Draft.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken) =>
        (await issues.RemoveAsync(id, cancellationToken)).ToActionResult(_ => NoContent());
}

/// <summary>The types of a use issue (the "Loại xuất" list of the settings). Reads as the item catalogue, writes Owner only; the system types cannot be edited or deleted (409 MSG-INPUT-02).</summary>
[ApiController]
[Route("api/input-issue-types")]
[Authorize]
public sealed class InputIssueTypesController(InputIssueTypeService types) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<InputIssueTypeView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => (await types.ListAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>409 MSG-GEN-02 (field Name) when a listed type has the name, regardless of letter case.</summary>
    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<InputIssueTypeView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveInputIssueTypeRequest request, CancellationToken cancellationToken) =>
        (await types.CreateAsync(request, cancellationToken)).ToActionResult(t => StatusCode(StatusCodes.Status201Created, t));

    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<InputIssueTypeView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveInputIssueTypeRequest request, CancellationToken cancellationToken) =>
        (await types.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Deleted when no issue has the type, otherwise hidden: no longer listed, still named on its issues.</summary>
    [HttpDelete("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await types.DeleteAsync(id, cancellationToken)).ToActionResult(_ => NoContent());
}

/// <summary>The movements ("Nhập – xuất") and seed lots of an input item, and the deletion of an item or of a seed lot.</summary>
[ApiController]
[Route("api/farming-input-items/{id:guid}")]
[Authorize]
public sealed class InputItemMovementsController(InputMovementService movements, FarmingInputItemDeletionService deletions) : ControllerBase
{
    [HttpGet("movements")]
    [ProducesResponseType<InputMovementPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Movements(Guid id, [FromQuery] InputMovementQuery query, CancellationToken cancellationToken) =>
        (await movements.MovementsAsync(id, query, cancellationToken)).ToActionResult(Ok);

    [HttpGet("movements/export")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> ExportMovements(Guid id, [FromQuery] InputMovementQuery query, CancellationToken cancellationToken) =>
        (await movements.ExportAsync(id, query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>The seed lots of a Seed item, oldest first, with the quantity left of each, the quantity that has no lot and the lot an issue line defaults to.</summary>
    [HttpGet("seed-lots")]
    [ProducesResponseType<InputSeedLotsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedLots(Guid id, CancellationToken cancellationToken) =>
        (await movements.SeedLotsAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Deletes the item: what is left is written off by a Completed disposal ("Xóa vật tư") and the item disappears from the lists. Owner only.</summary>
    [HttpDelete]
    [RequireCooperativeOwner]
    [ProducesResponseType<InputItemDeleted>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await deletions.DeleteItemAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Writes off what is left of a seed lot by a Completed disposal. Owner only.</summary>
    [HttpDelete("seed-lots/{lotLineId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<InputItemDeleted>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteSeedLot(Guid id, Guid lotLineId, CancellationToken cancellationToken) =>
        (await deletions.DeleteSeedLotAsync(id, lotLineId, cancellationToken)).ToActionResult(Ok);
}
