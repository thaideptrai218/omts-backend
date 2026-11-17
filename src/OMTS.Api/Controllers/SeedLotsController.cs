using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.SeedLots;

namespace OMTS.Api.Controllers;

/// <summary>
/// The seed lots: one row per Seed line of a Completed purchase receipt, with the quantities received, returned, in the nursery, lost, seeded and left, and the state worked out from them. The rules are those of purchase
/// receipts, which follow the role only whatever their zone (BR-FARM-08): the PURCHASE_RECEIPTS View action reads, the Export action exports.
/// </summary>
[ApiController]
[Route("api/seed-lots")]
[Authorize]
public sealed class SeedLotsController(SeedLotService lots, SeedLotFileService files) : ControllerBase
{
    /// <summary>The lots the filters match, with the sums of every match and how many lots each state holds.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SeedLotRegisterPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] SeedLotListQuery query, CancellationToken cancellationToken) =>
        (await lots.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The list as filtered and sorted, as .xlsx.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] SeedLotListQuery query, CancellationToken cancellationToken) =>
        (await files.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>One lot (the receipt line id) with its nursery batches, seedings, returns, issues, cycles and harvest lots.</summary>
    [HttpGet("{lineId:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SeedLotDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid lineId, CancellationToken cancellationToken) =>
        (await lots.GetAsync(lineId, cancellationToken)).ToActionResult(Ok);
}
