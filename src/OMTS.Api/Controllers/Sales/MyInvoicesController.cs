using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.Invoices.Dtos;

namespace OMTS.Api.Controllers.Sales;

/// <summary>The invoices of the logged-in trader, with the kg of each cage. Only the trader's own data: an invoice of another profile answers 404.</summary>
[ApiController]
[Authorize]
[RequireTrader]
[Route("api/me/invoices")]
public sealed class MyInvoicesController(MyInvoiceService invoices) : ControllerBase
{
    /// <summary>Newest first, <c>pageSize</c> up to 100; MSG-GEN-11 in <c>message</c> when the trader has no invoice yet.</summary>
    [HttpGet]
    [ProducesResponseType<MyInvoiceList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        (await invoices.ListAsync(page, pageSize, cancellationToken)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<MyInvoiceView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await invoices.GetAsync(id, cancellationToken)).ToActionResult(Ok);
}
