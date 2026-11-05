using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Api.Controllers;

/// <summary>
/// PURCHASE-01, PURCHASE-03, PURCHASE-06 – purchase receipts (and their returns until the PURCHASE-02 rework). Receipts have
/// no zone: they follow the role only (BR-FARM-08), so the filter checks the PURCHASE_RECEIPTS action of the role; the
/// handlers add the record rules (own drafts and own cancellations, rafts of the caller's zones).
/// </summary>
[ApiController]
[Authorize]
public sealed class PurchaseReceiptsController(
    PurchaseReceiptService receipts,
    PurchaseReceiptCancellationService cancellations,
    PurchaseReceiptCorrectionService corrections,
    PurchaseReceiptCopyService copies,
    PurchaseReturnService returns,
    PurchaseReceiptQueryService queries) : ControllerBase
{
    private const string Receipts = "api/purchase-receipts";

    /// <summary>PURCHASE-03: the receipts of the period (default: the current month) with the three totals.</summary>
    [HttpGet(Receipts)]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReceiptSearchResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] PurchaseReceiptQuery query, CancellationToken cancellationToken) =>
        (await queries.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>BR-FARM-05: the material cost of each raft in the period, with its lines and returns.</summary>
    [HttpGet(Receipts + "/raft-material-costs")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<RaftMaterialCostResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RaftMaterialCosts([FromQuery] PurchaseReceiptQuery query, CancellationToken cancellationToken) =>
        (await queries.RaftMaterialCostsAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>Picker of the raft filter: id, code and zone code of every raft, Discontinued ones included.</summary>
    [HttpGet(Receipts + "/raft-lookup")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<IReadOnlyList<RaftLookupItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RaftLookup(CancellationToken cancellationToken) =>
        Ok(await queries.RaftLookupAsync(cancellationToken));

    [HttpGet(Receipts + "/export")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] PurchaseReceiptQuery query, CancellationToken cancellationToken) =>
        (await queries.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>Any receipt, Drafts of others included (read-only).</summary>
    [HttpGet(Receipts + "/{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await receipts.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Saves a Draft, or completes it at once with <c>complete: true</c> (the Owner is notified, MSG-PURCHASE-01).</summary>
    [HttpPost(Receipts)]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SavePurchaseReceiptRequest request, CancellationToken cancellationToken) =>
        (await receipts.CreateAsync(request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>Continues an own Draft (the Owner: any); <c>rowVersion</c> is required.</summary>
    [HttpPut(Receipts + "/{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SavePurchaseReceiptRequest request, CancellationToken cancellationToken) =>
        (await receipts.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost(Receipts + "/{id:guid}/complete")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(Guid id, CompleteReceiptRequest? request, CancellationToken cancellationToken) =>
        (await receipts.CompleteAsync(id, request ?? new CompleteReceiptRequest(null), cancellationToken)).ToActionResult(Ok);

    /// <summary>PURCHASE-06.1: removes an own Draft (the Owner: any) with its lines.</summary>
    [HttpDelete(Receipts + "/{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Delete, PermissionScope.NoZone)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken) =>
        (await cancellations.RemoveAsync(id, cancellationToken)).ToActionResult(_ => NoContent());

    /// <summary>PURCHASE-06 steps 1–2: whether the receipt can be cancelled, and the supplier debt before and after.</summary>
    [HttpGet(Receipts + "/{id:guid}/cancel-preview")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Delete, PermissionScope.NoZone)]
    [ProducesResponseType<CancelPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewCancel(Guid id, CancellationToken cancellationToken) =>
        (await cancellations.PreviewAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>PURCHASE-06: cancels an own Completed receipt (the Owner: any) with a reason; its payments are kept.</summary>
    [HttpPost(Receipts + "/{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Delete, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelReceiptRequest request, CancellationToken cancellationToken) =>
        (await cancellations.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>PURCHASE-06.2: the Owner corrects a Completed receipt that can no longer be cancelled.</summary>
    [HttpPost(Receipts + "/{id:guid}/correct")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid id, CorrectReceiptRequest request, CancellationToken cancellationToken) =>
        (await corrections.CorrectAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>PURCHASE-01.4: a new receipt filled from this one, not saved; save it with <c>POST api/purchase-receipts</c>.</summary>
    [HttpGet(Receipts + "/{id:guid}/copy")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReceiptCopy>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Copy(Guid id, CancellationToken cancellationToken) =>
        (await copies.CopyAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost(Receipts + "/{id:guid}/returns")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateReturn(Guid id, CreatePurchaseReturnRequest request, CancellationToken cancellationToken) =>
        (await returns.CreateAsync(id, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    [HttpGet(Receipts + "/{id:guid}/returns")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<IReadOnlyList<ReceiptReturnView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Returns(Guid id, CancellationToken cancellationToken) =>
        (await receipts.GetAsync(id, cancellationToken)).ToActionResult(r => Ok(r.Returns));

    [HttpGet("api/purchase-returns/{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<PurchaseReturnView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReturn(Guid id, CancellationToken cancellationToken) =>
        (await returns.GetAsync(id, cancellationToken)).ToActionResult(Ok);
}
