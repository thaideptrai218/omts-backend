using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Labor;

namespace OMTS.Api.Controllers;

/// <summary>
/// LABOR-11 – job types and hourly rates: the Accountant and the Cooperative Owner (§I.4.4); a job type has no zone, so any
/// pair of the Accountant opens them (BR-FARM-08). A Zone Manager only reads the names to choose from, for a profile.
/// </summary>
[ApiController]
[Route("api/job-types")]
[Authorize]
public sealed class JobTypesController(JobTypeService jobTypes, PayRateService rates, JobTypeQueryService queries) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<Paged<JobTypeView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(string? search, int? statusId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await queries.SearchAsync(search, statusId, page, pageSize, cancellationToken));

    /// <summary>LABOR-01: the job types In Use, by name, without rates.</summary>
    [HttpGet("options")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<JobTypeOption>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Options(CancellationToken cancellationToken) => Ok(await queries.OptionsAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<JobTypeView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await queries.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-11.1: the job type with its first rate.</summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<JobTypeSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Add(AddJobTypeRequest request, CancellationToken cancellationToken) =>
        (await jobTypes.AddAsync(request, cancellationToken)).ToActionResult(j => StatusCode(StatusCodes.Status201Created, j));

    /// <summary>LABOR-11.0: a new rate from its date; the date of an existing rate replaces it. Ask MSG-LABOR-27 first.</summary>
    [HttpPost("{id:guid}/rates")]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<JobTypeSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangeRate(Guid id, ChangeRateRequest request, CancellationToken cancellationToken) =>
        (await rates.ChangeRateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-11.3. Ask MSG-GEN-04 first.</summary>
    [HttpPut("{id:guid}/name")]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<JobTypeSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rename(Guid id, RenameJobTypeRequest request, CancellationToken cancellationToken) =>
        (await jobTypes.RenameAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-11.2. Ask MSG-GEN-04 first.</summary>
    [HttpPost("{id:guid}/discontinue")]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<JobTypeSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid id, JobTypeStatusRequest request, CancellationToken cancellationToken) =>
        (await jobTypes.DiscontinueAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-11.2. Ask MSG-GEN-04 first.</summary>
    [HttpPost("{id:guid}/resume")]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<JobTypeSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(Guid id, JobTypeStatusRequest request, CancellationToken cancellationToken) =>
        (await jobTypes.ResumeAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
