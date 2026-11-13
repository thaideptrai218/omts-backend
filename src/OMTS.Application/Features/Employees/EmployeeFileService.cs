using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using C = OMTS.Application.Features.Employees.EmployeeFileColumns;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// Employee export, template and import as .xlsx (LABOR-01), the way <c>SupplierFileService</c> does suppliers. The personal and
/// bank columns are in the export only for a caller who maintains employees, and filled only on the rows they maintain; the
/// bank account number and the ID number are never in a file. Text cells are written as text, so a value such as =1+1 stays text.
/// </summary>
public sealed class EmployeeFileService(
    EmployeeQueryService queries,
    EmployeeImportService import,
    ISpreadsheetWriter spreadsheets,
    ISpreadsheetReader reader,
    TimeProvider timeProvider)
{
    public const long MaxImportBytes = 2L * 1024 * 1024;
    public const int MaxImportRows = 1000;

    private const string SheetName = "Nhân viên";

    public async Task<Result<ExportedFile>> ExportAsync(EmployeeExportQuery query, CancellationToken cancellationToken)
    {
        var found = await queries.ExportRowsAsync(query, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var (items, caller) = found.Value;
        var today = timeProvider.Today();
        // The personal group is there for a caller who maintains any employee; each row fills it only if they maintain that one.
        var personal = !caller.Zones.IsEmpty;
        var headers = new List<string> { C.Code, C.Name, C.Phone, C.Type, C.JobTypes, C.Zones, C.Status, C.LastWorkingDay };
        if (personal)
        {
            headers.AddRange([C.Birthday, C.Gender, C.StartDate, C.Address, C.Area, C.Ward, C.Bank, C.AccountHolder]);
        }

        var rows = items.Select(e => (IReadOnlyList<object?>)Row(e, today, personal && caller.Maintains(e), personal)).ToList();
        return Write(new SpreadsheetSheet(SheetName, headers, rows), $"employees-{today:yyyyMMdd}.xlsx");
    }

    public Result<ExportedFile> Template() => Write(new SpreadsheetSheet(SheetName, C.Import, []), "employees-import-template.xlsx");

    /// <summary>
    /// Checks the file, then imports the rows without an error and lists the others: the columns are matched by header (others
    /// ignored), at most <see cref="MaxImportRows"/> rows. Only the Cooperative Owner may import.
    /// </summary>
    public async Task<Result<EmployeeImportResult>> ImportAsync(IncomingFile? file, CancellationToken cancellationToken)
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
        var missing = C.RequiredOnImport.Where(c => !columns.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            return AppError.Invalid("File", $"The columns {string.Join(", ", missing)} are required.");
        }

        if (content.Rows.Count == 0)
        {
            return AppError.Invalid("File", "The file has no rows.");
        }

        var rows = content.Rows.Select(row => new EmployeeFileRow(
            row.Number, row.HasFormula, column => columns.TryGetValue(column, out var index) && index < row.Cells.Count ? row.Cells[index] : null)).ToList();
        return await import.ImportAsync(rows, cancellationToken);
    }

    private static List<object?> Row(Employee e, DateOnly today, bool showPersonal, bool hasPersonalColumns)
    {
        var row = new List<object?>
        {
            e.Code,
            e.FullName,
            e.PhoneNumber,
            C.TypeLabel(EmployeeTypePeriod.TypeOn(e.TypePeriods, today)),
            string.Join(", ", e.JobTypes.Where(j => j.JobType is not null).Select(j => j.JobType.Name).Order()),
            string.Join(", ", e.WorkingZones.Where(w => w.Zone is not null).Select(w => w.Zone.Code).Order()),
            C.StatusLabel(e.StatusId),
            e.LastWorkingDay,
        };
        if (hasPersonalColumns)
        {
            row.AddRange(showPersonal
                ? new object?[]
                {
                    e.Birthday, C.GenderLabel(e.Gender), e.StartDate, e.Address, e.Area, e.Ward,
                    e.BankAccount?.BankName, e.BankAccount?.AccountHolderName,
                }
                : new object?[9]);
        }

        return row;
    }

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

/// <summary>One data row of an import file: its number in the sheet, whether a cell holds a formula, and the text of a column by header (null when empty or absent).</summary>
public sealed record EmployeeFileRow(int Number, bool HasFormula, Func<string, string?> Cell);
