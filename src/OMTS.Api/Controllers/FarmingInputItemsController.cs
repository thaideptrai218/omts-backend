using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>
/// The catalogue of farming input items. Writes are Cooperative Owner only; reads are open to callers who view purchase receipts, supplier payments or cultivation cycles; the export
/// follows the right to export purchase receipts (the Owner and the Accountant).
/// </summary>
[ApiController]
[Route("api/farming-input-items")]
[Authorize]
public sealed class FarmingInputItemsController(FarmingInputItemService items, FarmingInputItemFileService files) : ControllerBase
{
    /// <summary>The items that are not deleted, by code; an array for the pickers (no photos). Use <c>/search</c> for the paged list with filters.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FarmingInputItemListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken) =>
        (await items.ListAsync(search, group, statusId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The paged list with the quantity on hand, filters, sort and the stock value of every match.</summary>
    [HttpGet("search")]
    [ProducesResponseType<FarmingInputItemPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] FarmingInputItemListQuery query, CancellationToken cancellationToken) =>
        (await items.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The list as filtered and sorted, as .xlsx.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] FarmingInputItemListQuery query, CancellationToken cancellationToken) =>
        (await files.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    [HttpGet("import/template")]
    [RequireCooperativeOwner]
    public IActionResult ImportTemplate() => files.Template().ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>Multipart field <c>file</c>: .xlsx, at most 2 MB and 1000 rows. Every row is checked first; rejected rows are listed in <c>errors</c> and nothing is saved while there are any.</summary>
    [HttpPost("import")]
    [RequireCooperativeOwner]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [UploadField("File")]
    [ProducesResponseType<InputItemImportResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken cancellationToken) =>
        (await files.ImportAsync(file is null ? null : await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);

    /// <summary>The code a new item of the type would get (GI000001 for Seed, VT000001 for Material, HK000001 for Other); not reserved.</summary>
    [HttpGet("next-code")]
    [RequireCooperativeOwner]
    [ProducesResponseType<NewFarmingInputItemProposal>(StatusCodes.Status200OK)]
    public async Task<IActionResult> NextCode(InputItemGroup group, CancellationToken cancellationToken) =>
        Ok(await items.ProposeAsync(Enum.IsDefined(group) ? group : InputItemGroup.Material, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await items.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveFarmingInputItemRequest request, CancellationToken cancellationToken) =>
        (await items.CreateAsync(request, cancellationToken)).ToActionResult(i => StatusCode(StatusCodes.Status201Created, i));

    /// <summary>Replaces every field of an In Use item; the row version is required.</summary>
    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveFarmingInputItemRequest request, CancellationToken cancellationToken) =>
        (await items.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/discontinue")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid id, CancellationToken cancellationToken) =>
        (await items.DiscontinueAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Resume (PURCHASE-08.2). <c>/reactivate</c> is the older route of the same action.</summary>
    [HttpPost("{id:guid}/resume")]
    [HttpPost("{id:guid}/reactivate")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken) =>
        (await items.ReactivateAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart field <c>file</c>: JPEG or PNG, at most 2 MB; an item keeps at most five photos.</summary>
    [HttpPost("{id:guid}/photos")]
    [RequireCooperativeOwner]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [UploadField("Photo")]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddPhoto(Guid id, IFormFile? file, CancellationToken cancellationToken) =>
        (await items.AddPhotoAsync(id, file is null ? null : await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);

    [HttpDelete("{id:guid}/photos/{photoId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemovePhoto(Guid id, Guid photoId, CancellationToken cancellationToken) =>
        (await items.RemovePhotoAsync(id, photoId, cancellationToken)).ToActionResult(Ok);
}

/// <summary>The unit catalogue of the input items (the "Đơn vị tính" list of the settings). Reads as the items, writes Owner only.</summary>
[ApiController]
[Route("api/input-units")]
[Authorize]
public sealed class InputUnitsController(InputUnitService units) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<InputUnitView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => (await units.ListAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>409 MSG-GEN-02 (field Name) when a unit has the name, regardless of letter case.</summary>
    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<InputUnitView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveInputUnitRequest request, CancellationToken cancellationToken) =>
        (await units.CreateAsync(request, cancellationToken)).ToActionResult(u => StatusCode(StatusCodes.Status201Created, u));

    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<InputUnitView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveInputUnitRequest request, CancellationToken cancellationToken) =>
        (await units.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Deletes a unit no item uses; 409 MSG-GEN-06 otherwise.</summary>
    [HttpDelete("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await units.DeleteAsync(id, cancellationToken)).ToActionResult(_ => NoContent());
}

/// <summary>The user groups of the input items ("Nhóm hàng"). Reads as the items, writes Owner only.</summary>
[ApiController]
[Route("api/input-item-groups")]
[Authorize]
public sealed class InputItemGroupsController(InputItemGroupService groups) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<InputItemGroupView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => (await groups.ListAsync(cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<InputItemGroupView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveInputItemGroupRequest request, CancellationToken cancellationToken) =>
        (await groups.CreateAsync(request, cancellationToken)).ToActionResult(g => StatusCode(StatusCodes.Status201Created, g));

    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<InputItemGroupView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveInputItemGroupRequest request, CancellationToken cancellationToken) =>
        (await groups.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary><c>ids</c> names every group once, in the new order.</summary>
    [HttpPost("reorder")]
    [RequireCooperativeOwner]
    [ProducesResponseType<IReadOnlyList<InputItemGroupView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reorder(ReorderInputItemGroupsRequest request, CancellationToken cancellationToken) =>
        (await groups.ReorderAsync(request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Deletes a group no item belongs to; 409 MSG-GEN-06 otherwise.</summary>
    [HttpDelete("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await groups.DeleteAsync(id, cancellationToken)).ToActionResult(_ => NoContent());
}

/// <summary>How the cost of input items is kept and whether stock may go below zero; read as the items, saved by the Owner only.</summary>
[ApiController]
[Authorize]
public sealed class InputInventorySettingsController(InputInventorySettingsService settings) : ControllerBase
{
    [HttpGet("api/input-inventory-settings")]
    [ProducesResponseType<InputInventorySettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => (await settings.GetAsync(cancellationToken)).ToActionResult(Ok);

    [HttpPut("api/input-inventory-settings")]
    [RequireCooperativeOwner]
    [ProducesResponseType<InputInventorySettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(UpdateInputInventorySettingsRequest request, CancellationToken cancellationToken) =>
        (await settings.UpdateAsync(request, cancellationToken)).ToActionResult(Ok);
}

/// <summary>"Thiết lập giá" of a purchase receipt: the cost price and the last purchase price of its items; the update is Owner only.</summary>
[ApiController]
[Authorize]
public sealed class ReceiptPriceSetupController(ReceiptPriceSetupService priceSetup) : ControllerBase
{
    [HttpGet("api/purchase-receipts/{id:guid}/price-setup")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<ReceiptPriceSetupView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => (await priceSetup.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPut("api/purchase-receipts/{id:guid}/price-setup")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ReceiptPriceSetupView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, UpdateReceiptPriceSetupRequest request, CancellationToken cancellationToken) =>
        (await priceSetup.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
