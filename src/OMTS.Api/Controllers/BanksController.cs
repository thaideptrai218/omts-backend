using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Filters;
using OMTS.Application.Features.ReceivingBankAccounts;

namespace OMTS.Api.Controllers;

/// <summary>The list of banks that the forms with a bank account use (customer, receiving account, cash account); any signed-in staff account may read it. It is a reference list: the receiving bank account itself stays with the Owner (<see cref="ReceivingBankAccountController"/>).</summary>
[ApiController]
[Authorize]
[RequireStaff]
public sealed class BanksController : ControllerBase
{
    [HttpGet("api/banks")]
    [ProducesResponseType<IReadOnlyList<BankView>>(StatusCodes.Status200OK)]
    public IActionResult Banks() => Ok(BankCatalog.All);
}
