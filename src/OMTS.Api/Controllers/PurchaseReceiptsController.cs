using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>
/// INP-03…INP-05, INP-07 – purchase receipts, returns, raft material cost and export. The filter checks the
/// PURCHASE_RECEIPTS action in some zone; each handler re-checks the zone of the receipt or of the filter.
/// </summary>
[ApiController]
[Authorize]
public sealed class PurchaseReceiptsController(
    PurchaseReceiptService receipts,
    PurchaseReceiptCancellationService cancellations,
    PurchaseReturnService returns,
    PurchaseReceiptQueryService queries) : ControllerBase
{
    private const string Receipts = "api/purchase-receipts";

    [HttpGet(Receipts)]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseReceiptSearchResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] PurchaseReceiptQuery query, CancellationToken cancellationToken) =>
        (await queries.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    [HttpGet(Receipts + "/raft-material-costs")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<RaftMaterialCostRow>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RaftMaterialCosts([FromQuery] PurchaseReceiptQuery query, CancellationToken cancellationToken) =>
        (await queries.RaftMaterialCostsAsync(query, cancellationToken)).ToActionResult(Ok);

    [HttpGet(Receipts + "/export")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] PurchaseReceiptQuery query, CancellationToken cancellationToken) =>
        (await queries.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    [HttpGet(Receipts + "/{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await receipts.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Saves a Draft, or completes it at once with <c>complete: true</c>.</summary>
    [HttpPost(Receipts)]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SavePurchaseReceiptRequest request, CancellationToken cancellationToken) =>
        (await receipts.CreateAsync(request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    [HttpPut(Receipts + "/{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SavePurchaseReceiptRequest request, CancellationToken cancellationToken) =>
        (await receipts.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost(Receipts + "/{id:guid}/complete")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(Guid id, CompleteReceiptRequest? request, CancellationToken cancellationToken) =>
        (await receipts.CompleteAsync(id, request ?? new CompleteReceiptRequest(null), cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart field <c>files</c>: JPEG, PNG or PDF invoice photos, at most 10 MB each.</summary>
    [HttpPost(Receipts + "/{id:guid}/invoice-photos")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Edit, PermissionScope.AnyZone)]
    [RequestSizeLimit(105 * 1024 * 1024)]
    [UploadField("Invoice photos")]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddInvoicePhotos(Guid id, [FromForm] IFormFileCollection files, CancellationToken cancellationToken) =>
        (await receipts.AddInvoicePhotosAsync(id, await SuppliersController.IncomingAsync(files, cancellationToken), cancellationToken)).ToActionResult(Ok);

    [HttpPost(Receipts + "/{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Delete, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelReceiptRequest request, CancellationToken cancellationToken) =>
        (await cancellations.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>A new Draft copied from a Completed or Cancelled receipt.</summary>
    [HttpPost(Receipts + "/{id:guid}/copy")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Copy(Guid id, CancellationToken cancellationToken) =>
        (await cancellations.CopyAsync(id, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    [HttpPost(Receipts + "/{id:guid}/returns")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseReceiptView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateReturn(Guid id, CreatePurchaseReturnRequest request, CancellationToken cancellationToken) =>
        (await returns.CreateAsync(id, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    [HttpGet(Receipts + "/{id:guid}/returns")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<ReceiptReturnView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Returns(Guid id, CancellationToken cancellationToken) =>
        (await receipts.GetAsync(id, cancellationToken)).ToActionResult(r => Ok(r.Returns));

    [HttpGet("api/purchase-returns/{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseReturnView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReturn(Guid id, CancellationToken cancellationToken) =>
        (await returns.GetAsync(id, cancellationToken)).ToActionResult(Ok);
}
