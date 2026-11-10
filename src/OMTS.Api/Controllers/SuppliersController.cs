using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing.Suppliers;

namespace OMTS.Api.Controllers;

/// <summary>
/// PURCHASE-07 – the cooperative-wide supplier list. Suppliers have no zone, so they follow the role only (BR-FARM-08): a
/// pair of a role holding SUPPLIERS in any zone opens every supplier (Zone Manager: maintain; Accountant: view). Supplier
/// debt is PURCHASE-04: <see cref="SupplierDebtsController"/>.
/// </summary>
[ApiController]
[Route("api/suppliers")]
[Authorize]
public sealed class SuppliersController(SupplierService suppliers, SupplierFileService files) : ControllerBase
{
    /// <summary>Total purchased and current debt are null, and their sort and filter parameters refused, without the permission that reads them.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<Paged<SupplierListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] SupplierListQuery query, CancellationToken cancellationToken) =>
        (await suppliers.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The list as filtered and sorted, as .xlsx. Figures appear only for callers who may read them; the identity number never.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] SupplierListQuery query, CancellationToken cancellationToken) =>
        (await files.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    [HttpGet("import/template")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Create, PermissionScope.NoZone)]
    public IActionResult ImportTemplate() =>
        files.Template().ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>Multipart field <c>file</c>: .xlsx, at most 2 MB and 1000 rows. All rows or none; rejected rows are listed in <c>errors</c>.</summary>
    [HttpPost("import")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Create, PermissionScope.NoZone)]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [UploadField("File")]
    [ProducesResponseType<SupplierImportResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken cancellationToken) =>
        (await files.ImportAsync(file is null ? null : await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);

    /// <summary>Discontinues or resumes several suppliers; each id reports its own outcome.</summary>
    [HttpPost("bulk-status")]
    [RequirePermission(PermissionFunctions.Suppliers, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierBulkResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkStatus(SupplierBulkStatusRequest request, CancellationToken cancellationToken) =>
        (await suppliers.BulkStatusAsync(request, cancellationToken)).ToActionResult(Ok);

    /// <summary>The purchase receipts and returns of the supplier, newest first, for those who view receipts.</summary>
    [HttpGet("{id:guid}/history")]
    [RequirePermission(PermissionFunctions.PurchaseReceipts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<Paged<SupplierHistoryItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History(Guid id, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        (await suppliers.HistoryAsync(id, page, pageSize, cancellationToken)).ToActionResult(Ok);

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
