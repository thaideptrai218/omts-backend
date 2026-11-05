using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Customers;

namespace OMTS.Api.Controllers;

/// <summary>
/// SALE-04 – customer profiles have no zone, so a CUSTOMERS grant in any zone is enough (BR-FARM-08). Adding a customer
/// while creating an invoice (SALE-05.1) is the same <c>POST</c>.
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize]
public sealed class CustomersController(CustomerService customers, CustomerMergeService merges) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<Paged<CustomerListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(string? search, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await customers.SearchAsync(search, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await customers.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveCustomerRequest request, CancellationToken cancellationToken) =>
        (await customers.CreateAsync(request, cancellationToken)).ToActionResult(c => StatusCode(StatusCodes.Status201Created, c));

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveCustomerRequest request, CancellationToken cancellationToken) =>
        (await customers.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>SALE-04.3 steps 2 and 3: both profiles with their debts and what will count with the kept one, or the refusal.</summary>
    [HttpGet("{id:guid}/merge-preview")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerMergePreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MergePreview(Guid id, Guid? mergedCustomerId, CancellationToken cancellationToken) =>
        (await merges.PreviewAsync(id, mergedCustomerId, cancellationToken)).ToActionResult(Ok);

    /// <summary>SALE-04.3: merges the profile of the body into this one. It cannot be undone.</summary>
    [HttpPost("{id:guid}/merge")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Merge(Guid id, MergeCustomerRequest request, CancellationToken cancellationToken) =>
        (await merges.MergeAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
