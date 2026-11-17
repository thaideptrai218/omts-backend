using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Sales.Disposals;

namespace OMTS.Api.Controllers;

/// <summary>
/// The disposals of oysters (Xuất hủy hàu, XHS). Every action is checked by the service, which knows the zone of the disposal: the Owner does everything; a Zone Manager records, completes, edits and cancels the
/// disposals of its zones; the Accountant reads and exports.
/// </summary>
[ApiController]
[Route("api/product-disposals")]
[Authorize]
public sealed class ProductDisposalsController(ProductDisposalService disposals) : ControllerBase
{
    /// <summary>The disposals the caller may see, with filters, sort, paging and the total kg of the Completed ones.</summary>
    [HttpGet]
    [ProducesResponseType<ProductDisposalPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] ProductDisposalListQuery query, CancellationToken cancellationToken) =>
        (await disposals.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] ProductDisposalListQuery query, CancellationToken cancellationToken) =>
        (await disposals.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>The code the next disposal would get (XHS000001); not reserved.</summary>
    [HttpGet("next-code")]
    [ProducesResponseType<ProductDisposalNextCode>(StatusCodes.Status200OK)]
    public async Task<IActionResult> NextCode(CancellationToken cancellationToken) => (await disposals.NextCodeAsync(cancellationToken)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProductDisposalView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await disposals.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Saves a Draft or, with <c>complete</c>, records the disposal at once (its kg leave the stock of their products).</summary>
    [HttpPost]
    [ProducesResponseType<ProductDisposalView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveProductDisposalRequest request, CancellationToken cancellationToken) =>
        (await disposals.CreateAsync(request, cancellationToken)).ToActionResult(d => StatusCode(StatusCodes.Status201Created, d));

    /// <summary>Replaces a Draft or a Completed disposal (the row version is required; a Completed disposal writes its kg again).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<ProductDisposalView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveProductDisposalRequest request, CancellationToken cancellationToken) =>
        (await disposals.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<ProductDisposalView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(Guid id, CompleteProductDisposalRequest request, CancellationToken cancellationToken) =>
        (await disposals.CompleteAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<ProductDisposalView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelProductDisposalRequest request, CancellationToken cancellationToken) =>
        (await disposals.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Removes a Draft.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken) =>
        (await disposals.RemoveAsync(id, cancellationToken)).ToActionResult(_ => NoContent());
}
