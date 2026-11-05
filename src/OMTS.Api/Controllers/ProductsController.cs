using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Sales.Products;

namespace OMTS.Api.Controllers;

/// <summary>
/// SALE-11 – the price list. Writes are Cooperative Owner only. Reads: the Owner sees every product; a Zone Manager
/// (SALE-05) and a trader (SALE-01) see those In Use, the trader with no price; others are refused by the route filter.
/// </summary>
[ApiController]
[Route("api/products")]
[Authorize]
public sealed class ProductsController(ProductService products, ProductReadService reads) : ControllerBase
{
    [HttpGet]
    [RequireProductReader]
    [ProducesResponseType<IReadOnlyList<ProductView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string? search, int? statusId, CancellationToken cancellationToken) =>
        (await reads.ListAsync(search, statusId, cancellationToken)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    [RequireProductReader]
    [ProducesResponseType<ProductView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await reads.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveProductRequest request, CancellationToken cancellationToken) =>
        (await products.CreateAsync(request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveProductRequest request, CancellationToken cancellationToken) =>
        (await products.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/discontinue")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid id, CancellationToken cancellationToken) =>
        (await products.DiscontinueAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/resume")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(Guid id, CancellationToken cancellationToken) =>
        (await products.ResumeAsync(id, cancellationToken)).ToActionResult(Ok);
}
