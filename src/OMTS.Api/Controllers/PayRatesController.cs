using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.LaborCost.Dtos;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;
using static OMTS.Application.Features.Authorization.Models.PermissionScope;

namespace OMTS.Api.Controllers;

/// <summary>
/// LAB-06 pay rates (Accountant with PAY_RULES at All zones, or the Owner). No approval step: a saved rate is Approved and
/// applies from <c>validFrom</c>; an open-ended earlier version of the person is closed on validFrom − 1 day.
/// </summary>
[ApiController]
[Route("api/pay-rates")]
[Authorize]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
public sealed class PayRatesController(PayRateService payRates, PayRateQueryService queries) : ControllerBase
{
    /// <summary>Version history of one person: <c>employeeId</c> or <c>workerId</c>, newest first.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.PayRules, View, AllZones)]
    [ProducesResponseType<IReadOnlyList<PayRateView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History(Guid? employeeId, Guid? workerId, CancellationToken cancellationToken) =>
        (await queries.HistoryAsync(employeeId, workerId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Employed employees and In Use workers with their current rate (no personal data).</summary>
    [HttpGet("people")]
    [RequirePermission(PermissionFunctions.PayRules, View, AllZones)]
    [ProducesResponseType<IReadOnlyList<PayPersonListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> People(string? search, CancellationToken cancellationToken) =>
        Ok(await queries.PeopleAsync(search, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.PayRules, View, AllZones)]
    [ProducesResponseType<PayRateView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await queries.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Example: <c>{"employeeId":"…","salaryType":"PerWorkingDay","amount":8000000,"standardWorkingDays":26,
    /// "components":[{"kind":"Allowance","name":"Lunch","amount":30000,"basis":"PerPaidDay"}],"validFrom":"2026-11-01"}</c>.
    /// Workers: DailyWage only. Amounts not a multiple of 1,000 answer MSG41; an overlapping version MSG03.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.Create, AllZones)]
    [ProducesResponseType<PayRateSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SavePayRateRequest request, CancellationToken cancellationToken) =>
        (await payRates.CreateAsync(request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>One version per person with the same rate, all or nothing; refused people are listed in <c>failures</c>.</summary>
    [HttpPost("bulk")]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.Create, AllZones)]
    [ProducesResponseType<IReadOnlyList<PayRateSaved>>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Bulk(BulkPayRateRequest request, CancellationToken cancellationToken) =>
        (await payRates.BulkAsync(request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>Edits a version not used by a finalized payslip (else MSG11): <c>reason</c> and <c>rowVersion</c> required.</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.PayRules, Edit, AllZones)]
    [ProducesResponseType<PayRateSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SavePayRateRequest request, CancellationToken cancellationToken) =>
        (await payRates.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
