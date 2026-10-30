using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Sales;

namespace OMTS.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/sales-products")]
public sealed class SalesProductsController(SalesProductService products) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(bool includeUnavailable = false, CancellationToken ct = default) =>
        (await products.ListAsync(includeUnavailable, ct)).ToActionResult(Ok);

    [HttpPost]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Create(SaveSalesProduct input, CancellationToken ct) =>
        (await products.SaveAsync(null, input, ct)).ToActionResult(result => StatusCode(StatusCodes.Status201Created, result));

    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Update(Guid id, SaveSalesProduct input, CancellationToken ct) =>
        (await products.SaveAsync(id, input, ct)).ToActionResult(Ok);
}
