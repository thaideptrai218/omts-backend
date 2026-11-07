using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Traceability.Withdrawal;

namespace OMTS.Api.Controllers;

/// <summary>
/// TRACE-04 – withdraw the origin information of a Published harvest lot from the Trace lot screen (SCR-TRACE-01). Cooperative Owner only (BR-TRACE-06): the Zone Manager, the
/// Accountant and traders get 403 MSG-GEN-08. The lot is published again with <c>POST api/harvest-lots/{id}/publish</c> (TRACE-01.2, Owner only).
/// </summary>
[ApiController]
[Authorize]
[RequireCooperativeOwner]
public sealed class ProvenanceWithdrawalController(LotWithdrawalService withdrawals) : ControllerBase
{
    /// <summary>
    /// Step 1: the traders the withdrawal would notify now (one per customer profile, with their invoices), the walk-in invoices, MSG-TRACE-06 in <c>notice</c> when no current invoice
    /// sells the lot, and the MSG-GEN-04 question. 409 MSG-GEN-09 when the lot is not Published. Changes nothing.
    /// </summary>
    [HttpGet("api/harvest-lots/{lotId:guid}/withdraw-preview")]
    [ProducesResponseType<LotWithdrawalPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(Guid lotId, CancellationToken cancellationToken) =>
        (await withdrawals.PreviewAsync(lotId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Steps 2-7: saves the withdrawal time and the reason (the lot is Withdrawn), sends one MSG-TRACE-05 per trader by SMS and, with a trader account, in the app, and answers the result
    /// of each notice (<c>toCall</c> when the SMS was not sent; a failed send undoes nothing). 400 MSG-GEN-01 without a reason; 409 MSG-GEN-09 when the lot is not Published.
    /// </summary>
    [HttpPost("api/harvest-lots/{lotId:guid}/withdraw")]
    [ProducesResponseType<LotWithdrawn>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Withdraw(Guid lotId, WithdrawLotRequest request, CancellationToken cancellationToken) =>
        (await withdrawals.WithdrawAsync(lotId, request, cancellationToken)).ToActionResult(Ok);
}
