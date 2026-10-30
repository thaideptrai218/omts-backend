using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Sales;

namespace OMTS.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/me/purchase-requests")]
public sealed class MyPurchaseRequestsController(PurchaseRequestService requests) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string? status = null, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        (await requests.ListAsync(true, status, page, pageSize, ct)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        (await requests.GetAsync(id, true, ct)).ToActionResult(Ok);

    [HttpPost]
    public async Task<IActionResult> Submit(CreatePurchaseRequest input, CancellationToken ct) =>
        (await requests.SubmitAsync(input, ct)).ToActionResult(result => result.Replayed
            ? Ok(result.Request)
            : CreatedAtAction(nameof(Get), new { id = result.Request.Id }, result.Request));

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancelPurchaseRequest input, CancellationToken ct) =>
        (await requests.CancelAsync(id, input, ct)).ToActionResult(Ok);
}
