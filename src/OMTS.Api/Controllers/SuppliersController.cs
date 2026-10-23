using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Purchasing.Suppliers;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>INP-01 – the cooperative-wide supplier list: a SUPPLIERS grant in any zone is enough. Supplier debt is INP-06 (All zones).</summary>
[ApiController]
[Route("api/suppliers")]
[Authorize]
public sealed class SuppliersController(SupplierService suppliers, SupplierDebtService debts) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<Paged<SupplierListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        string? search, SupplierType? type, int? statusId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await suppliers.SearchAsync(search, type, statusId, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await suppliers.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveSupplierRequest request, CancellationToken cancellationToken) =>
        (await suppliers.CreateAsync(request, cancellationToken)).ToActionResult(s => StatusCode(StatusCodes.Status201Created, s));

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveSupplierRequest request, CancellationToken cancellationToken) =>
        (await suppliers.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/discontinue")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid id, CancellationToken cancellationToken) =>
        (await suppliers.DiscontinueAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/reactivate")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken) =>
        (await suppliers.ReactivateAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart field <c>files</c>: JPEG, PNG or PDF, at most 10 MB each and 10 per save.</summary>
    [HttpPost("{id:guid}/certificates")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [RequestSizeLimit(105 * 1024 * 1024)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddCertificates(Guid id, [FromForm] IFormFileCollection files, CancellationToken cancellationToken) =>
        (await suppliers.AddCertificatesAsync(id, await IncomingAsync(files, cancellationToken), cancellationToken)).ToActionResult(Ok);

    [HttpDelete("{id:guid}/certificates/{index:int}")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveCertificate(Guid id, int index, CancellationToken cancellationToken) =>
        (await suppliers.RemoveCertificateAsync(id, index, cancellationToken)).ToActionResult(Ok);

    /// <summary>Per supplier: payable, returned, paid, refunded, unallocated and outstanding (INP-06).</summary>
    [HttpGet("debts")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.AllZones)]
    [ProducesResponseType<IReadOnlyList<SupplierDebtRow>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Debts(CancellationToken cancellationToken) => Ok(await debts.ListAsync(cancellationToken));

    [HttpGet("{id:guid}/debt")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.AllZones)]
    [ProducesResponseType<SupplierDebtDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Debt(Guid id, CancellationToken cancellationToken) =>
        (await debts.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    internal static async Task<IReadOnlyList<IncomingFile>> IncomingAsync(IFormFileCollection files, CancellationToken cancellationToken)
    {
        var incoming = new List<IncomingFile>();
        foreach (var file in files)
        {
            incoming.Add(await file.ToIncomingFileAsync(cancellationToken));
        }

        return incoming;
    }
}
