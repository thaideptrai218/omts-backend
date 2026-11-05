using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.ReceivingBankAccounts;

namespace OMTS.Api.Controllers;

/// <summary>DEBT-01 – the receiving bank account of the VietQR codes; Cooperative Owner only (§I.4.4).</summary>
[ApiController]
[Authorize]
[RequireCooperativeOwner]
public sealed class ReceivingBankAccountController(ReceivingBankAccountService accounts) : ControllerBase
{
    /// <summary>The list of banks of the form.</summary>
    [HttpGet("api/banks")]
    [ProducesResponseType<IReadOnlyList<BankView>>(StatusCodes.Status200OK)]
    public IActionResult Banks() => Ok(BankCatalog.All);

    /// <summary>The current account, with <c>account</c> null when none is set yet, and the <c>rowVersion</c> to save with.</summary>
    [HttpGet("api/cooperative/receiving-bank-account")]
    [ProducesResponseType<ReceivingBankAccountView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        (await accounts.GetAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Checks the account and asks the Payment Notification Service to accept it (MSG-DEBT-02: 409 refused, 503 no answer).
    /// Without <c>confirmAccount</c> it then answers 409 MSG-DEBT-01; on Yes send the same request with the flag (MSG-GEN-05).
    /// </summary>
    [HttpPut("api/cooperative/receiving-bank-account")]
    [ProducesResponseType<ReceivingBankAccountSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(SaveReceivingBankAccountRequest request, CancellationToken cancellationToken) =>
        (await accounts.SaveAsync(request, cancellationToken)).ToActionResult(Ok);
}
