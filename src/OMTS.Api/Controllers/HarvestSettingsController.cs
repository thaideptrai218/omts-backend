using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Harvest;

namespace OMTS.Api.Controllers;

/// <summary>HARVEST-03 – the harvest settings (SCR-HARVEST-02); Cooperative Owner only, kept apart from the cooperative profile.</summary>
[ApiController]
[Authorize]
[RequireCooperativeOwner]
public sealed class HarvestSettingsController(HarvestSettingsService settings) : ControllerBase
{
    [HttpGet("api/harvest-settings")]
    [ProducesResponseType<HarvestSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        (await settings.GetAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>Saves both values (MSG-GEN-05). Ask for confirmation first, with the preview when the days are lowered. Lots recorded earlier keep their own kg per cage.</summary>
    [HttpPut("api/harvest-settings")]
    [ProducesResponseType<HarvestSettingsSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(UpdateHarvestSettingsRequest request, CancellationToken cancellationToken) =>
        (await settings.UpdateAsync(request, cancellationToken)).ToActionResult(Ok);

    /// <summary>How many lots for sale the next daily close check would close with these days, with MSG-HARVEST-03 when they are lowered.</summary>
    [HttpGet("api/harvest-settings/auto-close-preview")]
    [ProducesResponseType<AutoClosePreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview([FromQuery] int? days, CancellationToken cancellationToken) =>
        (await settings.PreviewAsync(days, cancellationToken)).ToActionResult(Ok);
}
