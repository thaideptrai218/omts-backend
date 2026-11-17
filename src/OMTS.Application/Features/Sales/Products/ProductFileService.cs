using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>
/// Product export, import and template as .xlsx. Text cells are written as text, so a value such as =1+1 stays text; an import file with a formula is refused. The import checks every row first (shape, groups,
/// codes and names against the file and the catalogue) and saves nothing while a row is refused; the rows are then staged through the product form's rules and saved in one save (all or nothing). Import is the Owner's (API filter).
/// </summary>
public sealed class ProductFileService(
    ProductService productService,
    ProductCodeService codeService,
    ProductReadService reads,
    IProductRepository products,
    IProductCategoryRepository categories,
    ISpreadsheetWriter spreadsheets,
    ISpreadsheetReader reader,
    TimeProvider timeProvider)
{
    public const long MaxImportBytes = 2L * 1024 * 1024;
    public const int MaxImportRows = 1000;

    private const string Code = "Code";
    private const string Name = "Name";
    private const string Group = "Group";
    private const string UnitPrice = "Unit price";
    private const string MinKg = "Min kg";
    private const string MaxKg = "Max kg";
    private const string Description = "Description";
    private const string Note = "Note";

    private static readonly string[] ImportColumns = [Code, Name, Group, UnitPrice, MinKg, MaxKg, Description, Note];

    public async Task<Result<ExportedFile>> ExportAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        var found = await reads.ExportRowsAsync(query, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var headers = new List<string> { Code, Name, Group, "Unit", "Other units", UnitPrice, MinKg, MaxKg, "On hand (kg)", Description, Note, "Status", "Created by", "Created at" };
        var rows = found.Value!.Select(p => (IReadOnlyList<object?>)new List<object?>
        {
            p.Code, p.Name, p.CategoryName, p.Unit,
            string.Join("; ", p.Units.Where(u => !u.IsBase).Select(u => $"{u.Name} = {u.Factor.ToString("0.###", CultureInfo.InvariantCulture)} {p.Unit} ({u.Code}) @ {u.SalePrice?.ToString("0", CultureInfo.InvariantCulture)}")),
            p.UnitPrice, p.MinKg, p.MaxKg, p.OnHandKg, p.Description, p.Note, p.Status, p.CreatedBy, p.CreatedAt.ToOffset(BusinessClock.UtcOffset).DateTime,
        }).ToList();
        return Write(new SpreadsheetSheet("Products", headers, rows), $"products-{timeProvider.Today():yyyyMMdd}.xlsx");
    }

    public Result<ExportedFile> Template() => Write(new SpreadsheetSheet("Products", ImportColumns, []), "products-import-template.xlsx");

    /// <summary>The columns of the template (matched by header, others ignored), at most <see cref="MaxImportRows"/> rows. Name and Unit price are required.</summary>
    public async Task<Result<ProductImportResult>> ImportAsync(IncomingFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return AppError.Invalid("File");
        }

        if (file.Length > MaxImportBytes)
        {
            return AppError.Invalid("File", "At most 2 MB.");
        }

        var content = reader.Read(file.Content, MaxImportRows);
        if (content is null)
        {
            return AppError.Invalid("File", "An .xlsx workbook is expected.");
        }

        if (content.TooManyRows)
        {
            return AppError.Invalid("File", $"At most {MaxImportRows} rows.");
        }

        var columns = content.Headers.Select((header, index) => (header, index))
            .Where(c => c.header.Length > 0).GroupBy(c => c.header, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        if (!columns.ContainsKey(Name) || !columns.ContainsKey(UnitPrice))
        {
            return AppError.Invalid("File", $"The columns {Name} and {UnitPrice} are required.");
        }

        if (content.Rows.Count == 0)
        {
            return AppError.Invalid("File", "The file has no rows.");
        }

        var formulaErrors = content.Rows.Where(r => r.HasFormula).Select(r => new ProductImportError(r.Number, [], "Formulas are not accepted.")).ToList();
        if (formulaErrors.Count > 0)
        {
            return new ProductImportResult(0, formulaErrors);
        }

        var groupByName = (await categories.ListAsync(cancellationToken)).ToDictionary(g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase);
        var requests = new List<(int Row, SaveProductRequest Request)>();
        var errors = new List<ProductImportError>();
        var codesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in content.Rows)
        {
            string? Cell(string column) => columns.TryGetValue(column, out var index) && index < row.Cells.Count ? NullIfEmpty(row.Cells[index]) : null;
            var invalid = new List<string>();
            Guid? groupId = null;
            if (Cell(Group) is { } groupName)
            {
                if (groupByName.TryGetValue(groupName, out var id))
                {
                    groupId = id;
                }
                else
                {
                    invalid.Add(Group);
                }
            }

            decimal? Number(string column)
            {
                if (Cell(column) is not { } text)
                {
                    return null;
                }

                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }

                invalid.Add(column);
                return null;
            }

            var (price, min, max) = (Number(UnitPrice), Number(MinKg), Number(MaxKg));
            var code = DocumentCodes.Typed(Cell(Code));
            if (code is not null && !codesInFile.Add(code))
            {
                invalid.Add(Code);
            }

            if (Cell(Name) is { } name && !namesInFile.Add(name))
            {
                invalid.Add(Name);
            }

            if (invalid.Count > 0)
            {
                errors.Add(new ProductImportError(row.Number, invalid, "Missing, invalid or repeated in this file."));
                continue;
            }

            requests.Add((row.Number, new SaveProductRequest(Cell(Name), price, null, code, groupId, Cell(Description), Cell(Note), min, max)));
        }

        // Nothing is saved while a row cannot be read; the other rows are checked against the catalogue once these are fixed.
        foreach (var (row, request) in requests)
        {
            if (request.Code is not null && await products.FindCodeOwnerAsync(request.Code, null, null, cancellationToken) is not null)
            {
                errors.Add(new ProductImportError(row, [Code], "Already belongs to a product or a unit."));
            }
            else if (request.Name is not null && await products.FindByNameAsync(request.Name, null, cancellationToken) is not null)
            {
                errors.Add(new ProductImportError(row, [Name], "Already belongs to a product."));
            }
        }

        if (errors.Count > 0)
        {
            return new ProductImportResult(0, errors.OrderBy(e => e.Row).ToList());
        }

        // All or nothing: every row is staged in one unit of work (the codes typed in the file are kept apart from the generated ones) and saved once. The first row the product rules refuse
        // ends the import with nothing saved, and its answer lists every row that fails.
        var reserved = requests.Select(r => r.Request.Code).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, request) in requests)
        {
            var staged = await productService.StageCreateAsync(request.Code is null ? WithCode(request, await codeService.NextAvoidingAsync(reserved, cancellationToken)) : request, cancellationToken);
            if (staged.Error is not null)
            {
                var fields = staged.Error.Details?.GetValueOrDefault("fields") as IReadOnlyList<string> ?? (staged.Error.Details?.GetValueOrDefault("field") is string f ? [f] : []);
                errors.Add(new ProductImportError(row, fields, staged.Error.Message.Text));
            }
        }

        if (errors.Count > 0)
        {
            productService.DiscardStaged();
            return new ProductImportResult(0, errors);
        }

        if (await productService.CommitStagedAsync(cancellationToken) is { } commitError)
        {
            return new ProductImportResult(0, [new ProductImportError(0, [], commitError.Message.Text)]);
        }

        return new ProductImportResult(requests.Count, []);
    }

    private static SaveProductRequest WithCode(SaveProductRequest r, string generated) =>
        new(r.Name, r.UnitPrice, r.RowVersion, generated, r.CategoryId, r.Description, r.Note, r.MinKg, r.MaxKg, r.Units);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Result<ExportedFile> Write(SpreadsheetSheet sheet, string fileName)
    {
        try
        {
            return new ExportedFile(fileName, ISpreadsheetWriter.ContentType, spreadsheets.Write([sheet]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }
}
