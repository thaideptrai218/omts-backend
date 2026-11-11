using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>
/// Input item export, import and template as .xlsx. Text cells are written as text, so a value such as =1+1 stays text; an import file with a formula is refused. The import checks every row
/// first (shape, groups, codes and names against the file and the catalogue) and saves nothing while a row is refused; the rows are then created one by one through the item form's rules.
/// </summary>
public sealed class FarmingInputItemFileService(
    FarmingInputItemService itemService,
    IFarmingInputItemRepository items,
    IInputItemCategoryRepository groups,
    ISpreadsheetWriter spreadsheets,
    ISpreadsheetReader reader,
    TimeProvider timeProvider)
{
    public const long MaxImportBytes = 2L * 1024 * 1024;
    public const int MaxImportRows = 1000;

    private const string Code = "Code";
    private const string Name = "Name";
    private const string Type = "Type";
    private const string Group = "Group";
    private const string Unit = "Unit";
    private const string Description = "Description";
    private const string Notes = "Notes";
    private const string MinQuantity = "Min quantity";
    private const string MaxQuantity = "Max quantity";
    private const string CostPrice = "Cost price";
    private const string OpeningQuantity = "Opening quantity";

    private static readonly string[] ImportColumns = [Code, Name, Type, Group, Unit, Description, Notes, MinQuantity, MaxQuantity, CostPrice, OpeningQuantity];

    private static readonly Dictionary<string, InputItemGroup> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Seed"] = InputItemGroup.Seed, ["Giống"] = InputItemGroup.Seed,
        ["Material"] = InputItemGroup.Material, ["Vật tư"] = InputItemGroup.Material,
        ["Other"] = InputItemGroup.Other, ["Khác"] = InputItemGroup.Other,
    };

    public async Task<Result<ExportedFile>> ExportAsync(FarmingInputItemListQuery query, CancellationToken cancellationToken)
    {
        var found = await itemService.ExportRowsAsync(query, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var headers = new List<string>
        {
            Code, Name, Type, Group, Unit, "Other units", Description, Notes, MinQuantity, MaxQuantity, CostPrice, "Last purchase price", "On hand", "Stock value", "Status", "Created by", "Created at",
        };
        var rows = found.Value!.Select(i => (IReadOnlyList<object?>)new List<object?>
        {
            i.Code, i.Name, i.Group.ToString(), i.ItemGroupName, i.Unit,
            string.Join("; ", i.Units.Where(u => !u.IsBase).Select(u => $"{u.Name} = {u.Factor.ToString("0.###", CultureInfo.InvariantCulture)} {i.Unit} ({u.Code})")),
            i.Description, i.Notes, i.MinQuantity, i.MaxQuantity, i.CostPrice, i.LastPurchasePrice, i.OnHand, i.StockValue, i.Status, i.CreatedBy,
            i.CreatedAt.ToOffset(BusinessClock.UtcOffset).DateTime,
        }).ToList();
        return Write(new SpreadsheetSheet("Items", headers, rows), $"input-items-{timeProvider.Today():yyyyMMdd}.xlsx");
    }

    public Result<ExportedFile> Template() => Write(new SpreadsheetSheet("Items", ImportColumns, []), "input-items-import-template.xlsx");

    /// <summary>The columns of the template (matched by header, others ignored), at most <see cref="MaxImportRows"/> rows. Name, Type and Unit are required.</summary>
    public async Task<Result<InputItemImportResult>> ImportAsync(IncomingFile? file, CancellationToken cancellationToken)
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
        if (!columns.ContainsKey(Name) || !columns.ContainsKey(Type) || !columns.ContainsKey(Unit))
        {
            return AppError.Invalid("File", $"The columns {Name}, {Type} and {Unit} are required.");
        }

        if (content.Rows.Count == 0)
        {
            return AppError.Invalid("File", "The file has no rows.");
        }

        var formulaErrors = content.Rows.Where(r => r.HasFormula).Select(r => new InputItemImportError(r.Number, [], "Formulas are not accepted.")).ToList();
        if (formulaErrors.Count > 0)
        {
            return new InputItemImportResult(0, formulaErrors);
        }

        var groupByName = (await groups.ListAsync(cancellationToken)).ToDictionary(g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase);
        var requests = new List<(int Row, SaveFarmingInputItemRequest Request)>();
        var errors = new List<InputItemImportError>();
        var codesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in content.Rows)
        {
            string? Cell(string column) => columns.TryGetValue(column, out var index) && index < row.Cells.Count ? NullIfEmpty(row.Cells[index]) : null;
            var invalid = new List<string>();
            var typeText = Cell(Type);
            InputItemGroup? type = typeText is not null && Types.TryGetValue(typeText, out var t) ? t : null;
            if (type is null)
            {
                invalid.Add(Type);
            }

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

            var (min, max, cost, opening) = (Number(MinQuantity), Number(MaxQuantity), Number(CostPrice), Number(OpeningQuantity));
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
                errors.Add(new InputItemImportError(row.Number, invalid, "Missing, invalid or repeated in this file."));
                continue;
            }

            requests.Add((row.Number, new SaveFarmingInputItemRequest(
                code, Cell(Name), type, Cell(Unit), Cell(Description), null, groupId, Cell(Notes), min, max, cost, opening)));
        }

        // Nothing is saved while a row cannot be read; the other rows are checked against the catalogue once these are fixed.
        foreach (var (row, request) in requests)
        {
            if (request.Code is not null && await items.FindCodeOwnerAsync(request.Code, null, null, cancellationToken) is not null)
            {
                errors.Add(new InputItemImportError(row, [Code], "Already belongs to an item or a unit."));
            }
            else if (request.Name is not null && await items.FindByNameAsync(request.Name, null, cancellationToken) is not null)
            {
                errors.Add(new InputItemImportError(row, [Name], "Already belongs to an item."));
            }
        }

        if (errors.Count > 0)
        {
            return new InputItemImportResult(0, errors.OrderBy(e => e.Row).ToList());
        }

        var imported = 0;
        foreach (var (row, request) in requests)
        {
            var created = await itemService.CreateAsync(request, cancellationToken);
            if (!created.IsSuccess)
            {
                var fields = created.Error!.Details?.GetValueOrDefault("fields") as IReadOnlyList<string> ?? (created.Error.Details?.GetValueOrDefault("field") is string f ? [f] : []);
                errors.Add(new InputItemImportError(row, fields, created.Error.Message.Text));
                continue;
            }

            imported++;
        }

        return new InputItemImportResult(imported, errors);
    }

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
