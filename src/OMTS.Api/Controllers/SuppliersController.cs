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

/// <summary>
/// PURCHASE-07 – the cooperative-wide supplier list. Suppliers have no zone, so they follow the role only (BR-FARM-08): a
/// pair of a role holding SUPPLIERS in any zone opens every supplier (Zone Manager: maintain; Accountant: view). Supplier
/// debt is PURCHASE-04 (SUPPLIER_PAYMENTS).
/// </summary>
[ApiController]
[Route("api/suppliers")]
[Authorize]
public sealed class SuppliersController(SupplierService suppliers, SupplierDebtService debts) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<Paged<SupplierListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        string? search, SupplierType? type, int? statusId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await suppliers.SearchAsync(search, type, statusId, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await suppliers.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveSupplierRequest request, CancellationToken cancellationToken) =>
        (await suppliers.CreateAsync(request, cancellationToken)).ToActionResult(s => StatusCode(StatusCodes.Status201Created, s));

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveSupplierRequest request, CancellationToken cancellationToken) =>
        (await suppliers.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/discontinue")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid id, CancellationToken cancellationToken) =>
        (await suppliers.DiscontinueAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Resume (PURCHASE-07.2). <c>/reactivate</c> is the older route of the same action.</summary>
    [HttpPost("{id:guid}/resume")]
    [HttpPost("{id:guid}/reactivate")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken) =>
        (await suppliers.ReactivateAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart field <c>files</c>: JPEG, PNG or PDF, at most 10 MB each and 10 per save.</summary>
    [HttpPost("{id:guid}/certificates")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.NoZone)]
    [RequestSizeLimit(105 * 1024 * 1024)]
    [UploadField("Certificate")]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddCertificates(Guid id, [FromForm] IFormFileCollection files, CancellationToken cancellationToken) =>
        (await suppliers.AddCertificatesAsync(id, await IncomingAsync(files, cancellationToken), cancellationToken)).ToActionResult(Ok);

    [HttpDelete("{id:guid}/certificates/{index:int}")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveCertificate(Guid id, int index, CancellationToken cancellationToken) =>
        (await suppliers.RemoveCertificateAsync(id, index, cancellationToken)).ToActionResult(Ok);

    /// <summary>Per supplier: payable, returned, paid, refunded, unallocated and outstanding (INP-06).</summary>
    [HttpGet("debts")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<IReadOnlyList<SupplierDebtRow>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Debts(CancellationToken cancellationToken) => Ok(await debts.ListAsync(cancellationToken));

    [HttpGet("{id:guid}/debt")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.NoZone)]
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
