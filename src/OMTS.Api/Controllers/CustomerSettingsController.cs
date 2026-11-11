using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Customers;

namespace OMTS.Api.Controllers;

/// <summary>
/// The customer settings: the debt warning and the fields a customer profile must have. Read by whoever views customers (the customer form and the invoice form read them),
/// saved by the Cooperative Owner only.
/// </summary>
[ApiController]
[Authorize]
public sealed class CustomerSettingsController(CustomerSettingsService settings) : ControllerBase
{
    [HttpGet("api/customer-settings")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => (await settings.GetAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>Saves every value; the row version is required.</summary>
    [HttpPut("api/customer-settings")]
    [RequireCooperativeOwner]
    [ProducesResponseType<CustomerSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(UpdateCustomerSettingsRequest request, CancellationToken cancellationToken) =>
        (await settings.UpdateAsync(request, cancellationToken)).ToActionResult(Ok);
}
