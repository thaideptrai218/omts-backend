using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers;

/// <summary>
/// Supplier export, import and template as .xlsx. The export carries the total purchased and the debt only for callers who
/// may read them; the identity number is never in a file. Text cells are written as text, so a value such as =1+1 stays text.
/// </summary>
public sealed class SupplierFileService(SupplierService suppliers, ISpreadsheetWriter spreadsheets, ISpreadsheetReader reader, TimeProvider timeProvider)
{
    public const long MaxImportBytes = 2L * 1024 * 1024;
    public const int MaxImportRows = 1000;

    private const string Name = "Name";
    private const string Group = "Group";
    private const string Phone = "Phone";
    private const string Email = "Email";
    private const string Province = "Province";
    private const string Ward = "Ward";
    private const string Address = "Address";
    private const string Company = "Company";
    private const string TaxCode = "Tax code";
    private const string Note = "Note";

    private static readonly string[] ImportColumns = [Name, Group, Phone, Email, Province, Ward, Address, Company, TaxCode, Note];

    private static readonly Dictionary<string, SupplierType> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Seed"] = SupplierType.Seed, ["Giống"] = SupplierType.Seed,
        ["Material"] = SupplierType.Material, ["Vật tư"] = SupplierType.Material,
        ["Other"] = SupplierType.Other, ["Khác"] = SupplierType.Other,
    };

    public async Task<Result<ExportedFile>> ExportAsync(SupplierListQuery query, CancellationToken cancellationToken)
    {
        var found = await suppliers.ExportRowsAsync(query, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var (items, access) = found.Value;
        var headers = new List<string> { "Code", Name, Group, Phone, Email, Province, Ward, Address, Company, TaxCode, Note, "Status", "Created by", "Created at" };
        if (access.TotalPurchased)
        {
            headers.Add("Total purchased");
        }

        if (access.CurrentDebt)
        {
            headers.Add("Current debt");
        }

        var rows = items.Select(s =>
        {
            var row = new List<object?>
            {
                s.Code, s.Name, s.SupplierType.ToString(), s.PhoneNumber, s.Email, s.Province, s.Ward, s.Address, s.CompanyName, s.TaxCode, s.Note, s.Status, s.CreatedBy,
                s.CreatedAt.ToOffset(BusinessClock.UtcOffset).DateTime,
            };
            if (access.TotalPurchased)
            {
                row.Add(s.TotalPurchased);
            }

            if (access.CurrentDebt)
            {
                row.Add(s.CurrentDebt);
            }

            return (IReadOnlyList<object?>)row;
        }).ToList();
        return Write(new SpreadsheetSheet("Suppliers", headers, rows), $"suppliers-{timeProvider.Today():yyyyMMdd}.xlsx");
    }

    public Result<ExportedFile> Template() => Write(new SpreadsheetSheet("Suppliers", ImportColumns, []), "suppliers-import-template.xlsx");

    /// <summary>The same columns as the export (matched by header, others ignored), at most <see cref="MaxImportRows"/> rows.</summary>
    public async Task<Result<SupplierImportResult>> ImportAsync(IncomingFile? file, CancellationToken cancellationToken)
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
        if (!columns.ContainsKey(Name) || !columns.ContainsKey(Group) || !columns.ContainsKey(Phone))
        {
            return AppError.Invalid("File", $"The columns {Name}, {Group} and {Phone} are required.");
        }

        if (content.Rows.Count == 0)
        {
            return AppError.Invalid("File", "The file has no rows.");
        }

        var formulaErrors = content.Rows.Where(r => r.HasFormula).Select(r => new SupplierImportError(r.Number, [], "Formulas are not accepted.")).ToList();
        if (formulaErrors.Count > 0)
        {
            return new SupplierImportResult(0, formulaErrors);
        }

        var requests = content.Rows.Select(row =>
        {
            string? Cell(string column) => columns.TryGetValue(column, out var index) && index < row.Cells.Count ? row.Cells[index] : null;
            return (row.Number, new SaveSupplierRequest(
                Cell(Name), Groups.TryGetValue(Cell(Group) ?? string.Empty, out var type) ? type : null, RestoredPhone(Cell(Phone)),
                Cell(Address), Cell(Email), null, Cell(Province), Cell(Ward), Cell(Note), Cell(Company), Cell(TaxCode)));
        }).ToList();
        return await suppliers.ImportRowsAsync(requests, cancellationToken);
    }

    /// <summary>A phone typed in a number cell loses its leading zero in the spreadsheet.</summary>
    private static string? RestoredPhone(string? phone) => phone is { Length: 9 } && phone.All(char.IsAsciiDigit) ? $"0{phone}" : phone;

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
