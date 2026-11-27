using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Purchasing.Items;

namespace OMTS.Api.Controllers;

/// <summary>
/// The input VAT rates of the cooperative ("Thuế GTGT" of the settings). Reads as the item catalogue (whoever views receipts, supplier payments or cultivation cycles); every write is
/// Cooperative Owner only and audited. The system rate KCT cannot be edited or deactivated: 409 MSG-GEN-09.
/// </summary>
[ApiController]
[Route("api/vat-rates")]
[Authorize]
public sealed class VatRatesController(VatRateService rates) : ControllerBase
{
    /// <summary>Active rates first, then by sort order; <c>active=true</c> (or false) keeps only those.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<VatRateView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool? active, CancellationToken cancellationToken) =>
        (await rates.ListAsync(active, cancellationToken)).ToActionResult(Ok);

    /// <summary>409 MSG-GEN-02 (field Name) when a rate has the name, regardless of letter case.</summary>
    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<VatRateView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveVatRateRequest request, CancellationToken cancellationToken) =>
        (await rates.CreateAsync(request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>Documents made before keep the percent they took; the ones made afterwards use the new one.</summary>
    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<VatRateView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveVatRateRequest request, CancellationToken cancellationToken) =>
        (await rates.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>The rate a new supply item starts with; 409 MSG-GEN-09 for KCT and for an inactive rate.</summary>
    [HttpPost("{id:guid}/default")]
    [RequireCooperativeOwner]
    [ProducesResponseType<VatRateView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken cancellationToken) =>
        (await rates.SetDefaultAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Takes the rate out of the pickers; the items that still have it move to <c>replacementId</c> (required then, 400 MSG-GEN-01 on <c>replacementId</c>). 409 MSG-GEN-09 for KCT and the default rate.
    /// </summary>
    [HttpPost("{id:guid}/deactivate")]
    [RequireCooperativeOwner]
    [ProducesResponseType<DeactivateVatRateResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(Guid id, DeactivateVatRateRequest request, CancellationToken cancellationToken) =>
        (await rates.DeactivateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/activate")]
    [RequireCooperativeOwner]
    [ProducesResponseType<VatRateView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken) =>
        (await rates.ActivateAsync(id, cancellationToken)).ToActionResult(Ok);
}
