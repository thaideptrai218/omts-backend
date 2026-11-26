using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Harvest.Plans;

namespace OMTS.Api.Controllers;

/// <summary>
/// The employee side of the harvest roster, for the mobile app: the employee is the one signed in (never an id the client chooses), staff or seasonal worker. It needs no function of the matrix, like
/// <c>/api/me/purchase-shifts</c>: any account linked to an employee may read and answer its own invitations.
/// </summary>
[ApiController]
[Authorize]
[Route("api/me/harvest-shifts")]
public sealed class MyHarvestShiftsController(HarvestPlanMyShiftService shifts) : ControllerBase
{
    /// <summary>My invitations (plan code, planned time, rafts and zones, my answer, how many confirmed): those of the plans still open, or, with <c>from</c> and <c>to</c> (at most 366 days), those planned in the period. 403 for an account with no employee.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MyHarvestShift>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        (await shifts.MineAsync(from, to, cancellationToken)).ToActionResult(Ok);

    /// <summary>I accept (<c>accept</c> true) or decline (<c>accept</c> false, <c>reason</c> optional); answering the same again changes nothing, and I may change my mind while the plan is open. Answers my invitation.</summary>
    [HttpPost("{rowId:guid}/response")]
    [ProducesResponseType<MyHarvestShift>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Respond(Guid rowId, RespondHarvestShiftRequest request, CancellationToken cancellationToken) =>
        (await shifts.RespondAsync(rowId, request, cancellationToken)).ToActionResult(Ok);
}
