using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Employees;

namespace OMTS.Api.Controllers;

/// <summary>LAB-01 – employee profiles; Cooperative Owner only. Personal data is returned only here.</summary>
[ApiController]
[Route("api/employees")]
[Authorize]
[RequireCooperativeOwner]
public sealed class EmployeesController(EmployeeService employees) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<Paged<EmployeeListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        string? search, int? statusId, Guid? zoneId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await employees.SearchAsync(search, statusId, zoneId, page, pageSize, cancellationToken));

    /// <summary>Staff accounts not yet linked to an employee.</summary>
    [HttpGet("linkable-accounts")]
    [ProducesResponseType<IReadOnlyList<LinkableAccount>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> LinkableAccounts(CancellationToken cancellationToken) =>
        Ok(await employees.LinkableAccountsAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<EmployeeView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await employees.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>With an optional <c>account</c> block the login account is created in the same save; its temporary password is returned once.</summary>
    [HttpPost]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveEmployeeRequest request, CancellationToken cancellationToken) =>
        (await employees.CreateAsync(request, cancellationToken)).ToActionResult(e => StatusCode(StatusCodes.Status201Created, e));

    [HttpPut("{id:guid}")]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveEmployeeRequest request, CancellationToken cancellationToken) =>
        (await employees.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/account")]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAccount(Guid id, NewEmployeeAccount request, CancellationToken cancellationToken) =>
        (await employees.CreateAccountAsync(id, request, cancellationToken)).ToActionResult(e => StatusCode(StatusCodes.Status201Created, e));

    [HttpPost("{id:guid}/end-employment")]
    [ProducesResponseType<EmploymentChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> EndEmployment(Guid id, EndEmploymentRequest request, CancellationToken cancellationToken) =>
        (await employees.EndEmploymentAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/reinstate")]
    [ProducesResponseType<EmploymentChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reinstate(Guid id, CancellationToken cancellationToken) =>
        (await employees.ReinstateAsync(id, cancellationToken)).ToActionResult(Ok);
}
