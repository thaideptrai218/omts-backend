using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>
/// The bank accounts and e-wallets of the cash book. They have no zone, so they follow the role only (BR-FARM-08): the Owner and
/// the Accountant. The list is also open to whoever may read the cash book, for the pickers of its forms.
/// </summary>
[ApiController]
[Route("api/cash-accounts")]
[Authorize]
public sealed class CashAccountsController(CashAccountService accounts) : ControllerBase
{
    [HttpGet]
    [RequireAnyPermission(PermissionFunctions.CashBookSettings, PermissionAction.View, PermissionFunctions.CashBookOperations, PermissionAction.View)]
    [ProducesResponseType<IReadOnlyList<CashAccountView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CashAccountKind? kind, bool includeInactive = false, CancellationToken cancellationToken = default) =>
        Ok(await accounts.ListAsync(kind, includeInactive, cancellationToken));

    /// <summary>The list of banks for the form: the one of <c>api/banks</c> is Owner-only, and the Accountant who keeps these accounts is not the Owner.</summary>
    [HttpGet("banks")]
    [RequirePermission(PermissionFunctions.CashBookSettings, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<IReadOnlyList<BankView>>(StatusCodes.Status200OK)]
    public IActionResult Banks() => Ok(CashAccountService.Banks());

    /// <summary>A Bank account needs a bank of the list (<c>bankBin</c>), an e-wallet its <c>providerName</c>; 409 MSG-GEN-02 (field AccountNumber) for the same provider and number.</summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.CashBookSettings, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<CashAccountView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveCashAccountRequest request, CancellationToken cancellationToken) =>
        (await accounts.CreateAsync(request, cancellationToken)).ToActionResult(a => StatusCode(StatusCodes.Status201Created, a));

    /// <summary>The account of the VietQR codes cannot change bank or number or be put out of use: 409 MSG-CASH-01.</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.CashBookSettings, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<CashAccountView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveCashAccountRequest request, CancellationToken cancellationToken) =>
        (await accounts.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>204 when deleted; 200 with the account put out of use when a voucher or a payment names it; 409 MSG-CASH-01 for the VietQR account.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(PermissionFunctions.CashBookSettings, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<CashAccountView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await accounts.DeleteAsync(id, cancellationToken)).ToActionResult(a => a is null ? NoContent() : Ok(a));
}
