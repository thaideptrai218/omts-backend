using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.QualityClaims;

namespace OMTS.Api.Controllers;

/// <summary>
/// CLAIM-02 (SCR-CLAIM-02): the Cooperative Owner responds; Zone Managers only view, the claims of invoices in their zones (§I.4.4 R, PM question). The Accountant has no right here.
/// </summary>
[ApiController]
[Route("api/quality-claims")]
[Authorize]
public sealed class QualityClaimsController(QualityClaimReadService reads, QualityClaimResponseService responses) : ControllerBase
{
    /// <summary>Those Awaiting response first, then newest first.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.QualityClaims, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<QualityClaimsPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(int? statusId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await reads.SearchAsync(statusId, page, pageSize, cancellationToken));

    /// <summary>The claim with its photos, the customer with the phone number, and the invoice with its lines, lots, kg, amounts, status and price cuts.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.QualityClaims, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<QualityClaimView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await reads.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Accepts with <c>offer</c> (<c>ReturnAllGoods</c> or <c>PriceCutWholeInvoice</c>), an optional <c>note</c> and <c>rowVersion</c>. Changes nothing on the invoice or the debt; push and SMS to the trader.</summary>
    [HttpPost("{id:guid}/accept")]
    [RequireCooperativeOwner]
    [ProducesResponseType<QualityClaimView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Accept(Guid id, AcceptQualityClaim request, CancellationToken cancellationToken) =>
        (await responses.AcceptAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Rejects with a required <c>note</c> and <c>rowVersion</c>; also when the invoice was cancelled or has a sales return.</summary>
    [HttpPost("{id:guid}/reject")]
    [RequireCooperativeOwner]
    [ProducesResponseType<QualityClaimView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(Guid id, RejectQualityClaim request, CancellationToken cancellationToken) =>
        (await responses.RejectAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
