using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Sales.Products;

namespace OMTS.Api.Controllers;

/// <summary>
/// SALE-11 – the product catalogue (the price list with its stock). Writes are Cooperative Owner only. Reads: the Owner sees every product; a Zone Manager (SALE-05) and a trader (SALE-01) see those In Use on the
/// plain list, the trader with no price; the catalogue screen (<c>search</c>, kg on hand, movements, lots, price history, export) is open to the Owner, the Zone Manager and the Accountant; others are refused.
/// </summary>
[ApiController]
[Route("api/products")]
[Authorize]
public sealed class ProductsController(ProductService products, ProductReadService reads, ProductMovementService movements, ProductFileService files) : ControllerBase
{
    /// <summary>The products that are not deleted, by name, as an array for the pickers. Use <c>/search</c> for the paged catalogue with filters and the kg on hand.</summary>
    [HttpGet]
    [RequireProductReader]
    [ProducesResponseType<IReadOnlyList<ProductView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string? search, int? statusId, CancellationToken cancellationToken) =>
        (await reads.ListAsync(search, statusId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The paged catalogue with the kg on hand, filters, sort and the total kg of every match.</summary>
    [HttpGet("search")]
    [ProducesResponseType<ProductPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] ProductListQuery query, CancellationToken cancellationToken) =>
        (await reads.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The list as filtered and sorted, as .xlsx.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] ProductListQuery query, CancellationToken cancellationToken) =>
        (await files.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    [HttpGet("import/template")]
    [RequireCooperativeOwner]
    public IActionResult ImportTemplate() => files.Template().ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>Multipart field <c>file</c>: .xlsx, at most 2 MB and 1000 rows. Every row is checked first; rejected rows are listed in <c>errors</c> and nothing is saved while there are any.</summary>
    [HttpPost("import")]
    [RequireCooperativeOwner]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [UploadField("File")]
    [ProducesResponseType<ProductImportResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken cancellationToken) =>
        (await files.ImportAsync(file is null ? null : await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);

    /// <summary>The code a new product would get (SP000001); not reserved.</summary>
    [HttpGet("next-code")]
    [RequireCooperativeOwner]
    [ProducesResponseType<NewProductProposal>(StatusCodes.Status200OK)]
    public async Task<IActionResult> NextCode(CancellationToken cancellationToken) => Ok(await reads.ProposeAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [RequireProductReader]
    [ProducesResponseType<ProductDetailView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await reads.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveProductRequest request, CancellationToken cancellationToken) =>
        (await products.CreateAsync(request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>Replaces every field of an In Use product; the row version is required. A new price adds a row to the price history.</summary>
    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveProductRequest request, CancellationToken cancellationToken) =>
        (await products.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/discontinue")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductDetailView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid id, CancellationToken cancellationToken) =>
        (await products.DiscontinueAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/resume")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductDetailView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(Guid id, CancellationToken cancellationToken) =>
        (await products.ResumeAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Removes a product nothing refers to; a product with history is hidden instead (Discontinued, in no list). <c>removed</c> says which.</summary>
    [HttpDelete("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductDeleted>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await products.DeleteAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart field <c>file</c>: JPEG or PNG, at most 2 MB; a product keeps at most five photos.</summary>
    [HttpPost("{id:guid}/photos")]
    [RequireCooperativeOwner]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [UploadField("Photo")]
    [ProducesResponseType<ProductDetailView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddPhoto(Guid id, IFormFile? file, CancellationToken cancellationToken) =>
        (await products.AddPhotoAsync(id, file is null ? null : await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);

    [HttpDelete("{id:guid}/photos/{photoId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductDetailView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemovePhoto(Guid id, Guid photoId, CancellationToken cancellationToken) =>
        (await products.RemovePhotoAsync(id, photoId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The movements of the product ("Nhập – xuất" tab) with the balance after each, newest first, in a period, optionally of one kind.</summary>
    [HttpGet("{id:guid}/movements")]
    [ProducesResponseType<ProductMovementPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Movements(Guid id, [FromQuery] ProductMovementQuery query, CancellationToken cancellationToken) =>
        (await movements.MovementsAsync(id, query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The movements of the product as .xlsx, with the same filters and zone scope as <c>/movements</c> (paging ignored).</summary>
    [HttpGet("{id:guid}/movements/export")]
    public async Task<IActionResult> ExportMovements(Guid id, [FromQuery] ProductMovementQuery query, CancellationToken cancellationToken) =>
        (await movements.ExportAsync(id, query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>The kg of the product on each harvest lot ("Lô thu hoạch" tab): harvested, sold, written off and left.</summary>
    [HttpGet("{id:guid}/harvest-lots")]
    [ProducesResponseType<ProductLotsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> HarvestLots(Guid id, CancellationToken cancellationToken) =>
        (await movements.LotsAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>The price history of the product ("Lịch sử giá" tab), newest first; <c>unitId</c> (the shared unit id of a unit row) limits it to one unit.</summary>
    [HttpGet("{id:guid}/price-history")]
    [ProducesResponseType<IReadOnlyList<ProductPriceChangeView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PriceHistory(Guid id, Guid? unitId, CancellationToken cancellationToken) =>
        (await movements.PriceHistoryAsync(id, unitId, cancellationToken)).ToActionResult(Ok);
}

/// <summary>The user groups of the products ("Nhóm hàng"). Reads as the catalogue, writes Owner only.</summary>
[ApiController]
[Route("api/product-categories")]
[Authorize]
public sealed class ProductCategoriesController(ProductCategoryService categories) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProductCategoryView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => (await categories.ListAsync(cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductCategoryView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveProductCategoryRequest request, CancellationToken cancellationToken) =>
        (await categories.CreateAsync(request, cancellationToken)).ToActionResult(g => StatusCode(StatusCodes.Status201Created, g));

    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ProductCategoryView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveProductCategoryRequest request, CancellationToken cancellationToken) =>
        (await categories.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary><c>ids</c> names every group once, in the new order.</summary>
    [HttpPost("reorder")]
    [RequireCooperativeOwner]
    [ProducesResponseType<IReadOnlyList<ProductCategoryView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reorder(ReorderProductCategoriesRequest request, CancellationToken cancellationToken) =>
        (await categories.ReorderAsync(request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Deletes a group no product belongs to; 409 MSG-GEN-06 otherwise.</summary>
    [HttpDelete("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await categories.DeleteAsync(id, cancellationToken)).ToActionResult(_ => NoContent());
}
