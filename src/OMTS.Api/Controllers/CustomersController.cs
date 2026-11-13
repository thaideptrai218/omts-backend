using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Customers;

namespace OMTS.Api.Controllers;

/// <summary>
/// SALE-04 – customer profiles have no zone, so a CUSTOMERS grant in any zone is enough (BR-FARM-08): the Zone Manager maintains them, the Accountant views, creates, edits and
/// exports them. Adding a customer while creating an invoice (SALE-05.1) is the same <c>POST</c>. The debt and sales figures of the list follow CUSTOMER_DEBTS View.
/// The sales history of a customer is <c>GET api/sales-invoices?customerId=…</c>, its purchase requests <c>GET api/purchase-requests?customerId=…</c>, its debt
/// <c>GET api/customer-debts/{id}</c>.
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize]
public sealed class CustomersController(CustomerService customers, CustomerFileService files, CustomerMergeService merges) : ControllerBase
{
    /// <summary>
    /// The customers that are not merged, In Use by default (what the pickers offer), with filters, sort, paging and the summary row. Debt and sales figures are null, and their sort and
    /// filter parameters refused, without CUSTOMER_DEBTS View.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerListPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] CustomerListQuery query, CancellationToken cancellationToken) =>
        (await customers.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The list as filtered and sorted, as .xlsx. Figures appear only for callers who may read them; the identity number never.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] CustomerListQuery query, CancellationToken cancellationToken) =>
        (await files.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    [HttpGet("import/template")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Create, PermissionScope.AnyZone)]
    public IActionResult ImportTemplate() =>
        files.Template().ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>Multipart field <c>file</c>: .xlsx, at most 2 MB and 1000 rows. All rows or none; rejected rows are listed in <c>errors</c>.</summary>
    [HttpPost("import")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Create, PermissionScope.AnyZone)]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [UploadField("File")]
    [ProducesResponseType<CustomerImportResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken cancellationToken) =>
        (await files.ImportAsync(file is null ? null : await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);

    /// <summary>Discontinues, resumes or deletes several customers; each id reports its own outcome. A delete is refused for a customer that was used.</summary>
    [HttpPost("bulk")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerBulkResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Bulk(CustomerBulkRequest request, CancellationToken cancellationToken) =>
        (await customers.BulkAsync(request, cancellationToken)).ToActionResult(Ok);

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

    /// <summary>Replaces every field of an In Use profile; the row version is required.</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveCustomerRequest request, CancellationToken cancellationToken) =>
        (await customers.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Deletes a customer nothing refers to; 409 MSG-GEN-06 (with <c>canDiscontinue</c>) otherwise.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Delete, PermissionScope.AnyZone)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await customers.DeleteAsync(id, cancellationToken)).ToActionResult(_ => NoContent());

    [HttpPost("{id:guid}/discontinue")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid id, CancellationToken cancellationToken) =>
        (await customers.DiscontinueAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/resume")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(Guid id, CancellationToken cancellationToken) =>
        (await customers.ResumeAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart field <c>file</c>: JPEG or PNG, at most 2 MB. Replaces the photo.</summary>
    [HttpPut("{id:guid}/photo")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [UploadField("Photo")]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetPhoto(Guid id, IFormFile? file, CancellationToken cancellationToken) =>
        (await customers.SetPhotoAsync(id, file is null ? null : await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);

    [HttpDelete("{id:guid}/photo")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemovePhoto(Guid id, CancellationToken cancellationToken) =>
        (await customers.RemovePhotoAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>SALE-04.3 steps 2 and 3: both profiles with their debts and what will count with the kept one, or the refusal.</summary>
    [HttpGet("{id:guid}/merge-preview")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerMergePreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MergePreview(Guid id, Guid? mergedCustomerId, CancellationToken cancellationToken) =>
        (await merges.PreviewAsync(id, mergedCustomerId, cancellationToken)).ToActionResult(Ok);

    /// <summary>SALE-04.3: merges the profile of the body into this one. It cannot be undone; the trader account of the merged profile is locked.</summary>
    [HttpPost("{id:guid}/merge")]
    [RequirePermission(PermissionFunctions.Customers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Merge(Guid id, MergeCustomerRequest request, CancellationToken cancellationToken) =>
        (await merges.MergeAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
