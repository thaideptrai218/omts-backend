using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>
/// The cash book: the book of each fund, the self-made vouchers and the people a voucher names. No zone, so it follows the role only
/// (BR-FARM-08): the Owner and the Accountant.
/// </summary>
[ApiController]
[Route("api/cash-book")]
[Authorize]
public sealed class CashBookController(
    CashBookService book, CashBookExportService exports, CashVoucherService vouchers, CashPartyService parties) : ControllerBase
{
    /// <summary>
    /// The lines of a fund (supplier payments with money included) with filters and paging, and the summary of the fund over the period:
    /// opening balance, money in, money out and closing balance; the summary ignores every filter but the fund, the account and the period.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.CashBookOperations, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<CashBookPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] CashBookQuery query, CancellationToken cancellationToken) =>
        (await book.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The same lines as the list, all of them, as an .xlsx file.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.CashBookOperations, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] CashBookQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    [HttpGet("parties")]
    [RequirePermission(PermissionFunctions.CashBookOperations, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<IReadOnlyList<CashPartyView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchParties(CashPartyKind kind, string? search, int take = 20, CancellationToken cancellationToken = default) =>
        Ok(await parties.SearchAsync(kind, search, take, cancellationToken));

    /// <summary>A payer or receiver who is neither a customer, a supplier nor an employee (kind Other).</summary>
    [HttpPost("parties")]
    [RequirePermission(PermissionFunctions.CashBookOperations, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<CashPartyView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateParty(CreateCashPartyRequest request, CancellationToken cancellationToken) =>
        (await parties.CreateAsync(request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>Who may collect or pay: the Active Owner and staff accounts, by name.</summary>
    [HttpGet("collectors")]
    [RequirePermission(PermissionFunctions.CashBookOperations, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<IReadOnlyList<AccountRef>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Collectors(CancellationToken cancellationToken) =>
        Ok(await parties.CollectorsAsync(cancellationToken));

    [HttpGet("vouchers/{id:guid}")]
    [RequirePermission(PermissionFunctions.CashBookOperations, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<CashVoucherView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVoucher(Guid id, CancellationToken cancellationToken) =>
        (await vouchers.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>A code left empty is the next of the sequence of the fund and direction; a typed one already used is 409 MSG-GEN-02 (field Code).</summary>
    [HttpPost("vouchers")]
    [RequirePermission(PermissionFunctions.CashBookOperations, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<CashVoucherView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateVoucher(SaveCashVoucherRequest request, CancellationToken cancellationToken) =>
        (await vouchers.CreateAsync(request, cancellationToken)).ToActionResult(v => StatusCode(StatusCodes.Status201Created, v));

    /// <summary>Everything on a Recorded voucher may change, its amount and fund included; the row version is required. 409 MSG-GEN-09 when it is cancelled.</summary>
    [HttpPut("vouchers/{id:guid}")]
    [RequirePermission(PermissionFunctions.CashBookOperations, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<CashVoucherView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateVoucher(Guid id, SaveCashVoucherRequest request, CancellationToken cancellationToken) =>
        (await vouchers.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>A voucher made by mistake, with an optional reason; 409 MSG-GEN-09 when it is already cancelled.</summary>
    [HttpPost("vouchers/{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.CashBookOperations, PermissionAction.Delete, PermissionScope.NoZone)]
    [ProducesResponseType<CashVoucherView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelVoucher(Guid id, CancelCashVoucherRequest request, CancellationToken cancellationToken) =>
        (await vouchers.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
