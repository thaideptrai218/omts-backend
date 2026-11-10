using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>
/// The voucher types of the cash book, with no zone (the Owner and the Accountant). The list is also open to whoever may read the
/// cash book, for the type picker of its forms. The system types cannot be edited or deleted: 409 MSG-CASH-02.
/// </summary>
[ApiController]
[Route("api/cash-voucher-types")]
[Authorize]
public sealed class CashVoucherTypesController(CashVoucherTypeService types) : ControllerBase
{
    [HttpGet]
    [RequireAnyPermission(PermissionFunctions.CashBookSettings, PermissionAction.View, PermissionFunctions.CashBookOperations, PermissionAction.View)]
    [ProducesResponseType<IReadOnlyList<CashVoucherTypeView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CashDirection? direction, CancellationToken cancellationToken) =>
        Ok(await types.ListAsync(direction, cancellationToken));

    /// <summary>409 MSG-GEN-02 (field Name) when a listed type of the direction has the name.</summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.CashBookSettings, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<CashVoucherTypeView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveCashVoucherTypeRequest request, CancellationToken cancellationToken) =>
        (await types.CreateAsync(request, cancellationToken)).ToActionResult(t => StatusCode(StatusCodes.Status201Created, t));

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.CashBookSettings, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<CashVoucherTypeView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveCashVoucherTypeRequest request, CancellationToken cancellationToken) =>
        (await types.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Deleted when no voucher has the type, otherwise hidden: no longer listed, still named on its vouchers.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(PermissionFunctions.CashBookSettings, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await types.DeleteAsync(id, cancellationToken)).ToActionResult(_ => NoContent());
}
