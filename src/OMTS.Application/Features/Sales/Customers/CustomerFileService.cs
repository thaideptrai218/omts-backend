using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>
/// Customer export, import and template as .xlsx. The export carries the debt and the sales only for callers who may read them (CUSTOMER_DEBTS View); the identity number
/// and the bank account are never in a file. Text cells are written as text, so a value such as =1+1 stays text.
/// </summary>
public sealed class CustomerFileService(CustomerService customers, ISpreadsheetWriter spreadsheets, ISpreadsheetReader reader, TimeProvider timeProvider)
{
    public const long MaxImportBytes = 2L * 1024 * 1024;
    public const int MaxImportRows = 1000;

    private const string Code = "Code";
    private const string Name = "Name";
    private const string Type = "Type";
    private const string Gender = "Gender";
    private const string BirthDate = "Birth date";
    private const string Phone = "Phone";
    private const string Phone2 = "Phone 2";
    private const string Email = "Email";
    private const string Facebook = "Facebook";
    private const string Province = "Province";
    private const string Ward = "Ward";
    private const string Address = "Address";
    private const string Company = "Company";
    private const string TaxCode = "Tax code";
    private const string Note = "Note";
    private const string Bank = "Bank";
    private const string BankAccount = "Bank account";

    private static readonly string[] ImportColumns = [Code, Name, Type, Gender, BirthDate, Phone, Phone2, Email, Facebook, Province, Ward, Address, Company, TaxCode, Note, Bank, BankAccount];

    private static readonly string[] DateFormats = ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"];

    private static readonly Dictionary<string, CustomerType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Individual"] = CustomerType.Individual, ["Cá nhân"] = CustomerType.Individual,
        ["Company"] = CustomerType.Company, ["Công ty"] = CustomerType.Company,
    };

    private static readonly Dictionary<string, Domain.Enums.Gender> Genders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Male"] = Domain.Enums.Gender.Male, ["Nam"] = Domain.Enums.Gender.Male,
        ["Female"] = Domain.Enums.Gender.Female, ["Nữ"] = Domain.Enums.Gender.Female,
    };

    public async Task<Result<ExportedFile>> ExportAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        var found = await customers.ExportRowsAsync(query, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var (items, access) = found.Value;
        var headers = new List<string>
        {
            Code, Name, Type, Gender, BirthDate, Phone, Phone2, Email, Facebook, Province, Ward, Address, Company, TaxCode, Note, "Status", "Last transaction", "Created by", "Created at",
        };
        if (access.Debt)
        {
            headers.AddRange(["Total sales", "Net sales", "Current debt"]);
        }

        var rows = items.Select(c =>
        {
            var row = new List<object?>
            {
                c.Code, c.Name, c.CustomerType.ToString(), c.Gender?.ToString(), c.BirthDate?.ToDateTime(TimeOnly.MinValue), c.PhoneNumber, c.PhoneNumber2, c.Email, c.Facebook,
                c.Province, c.Ward, c.Address, c.CompanyName, c.TaxCode, c.Note, c.Status, c.LastTransactionAt?.ToOffset(BusinessClock.UtcOffset).DateTime, c.CreatedBy,
                c.CreatedAt.ToOffset(BusinessClock.UtcOffset).DateTime,
            };
            if (access.Debt)
            {
                row.AddRange([c.TotalSales, c.NetSales, c.CurrentDebt]);
            }

            return (IReadOnlyList<object?>)row;
        }).ToList();
        return Write(new SpreadsheetSheet("Customers", headers, rows), $"customers-{timeProvider.Today():yyyyMMdd}.xlsx");
    }

    public Result<ExportedFile> Template() => Write(new SpreadsheetSheet("Customers", ImportColumns, []), "customers-import-template.xlsx");

    /// <summary>The same columns as the export (matched by header, others ignored), at most <see cref="MaxImportRows"/> rows. Only Name and Phone are required.</summary>
    public async Task<Result<CustomerImportResult>> ImportAsync(IncomingFile? file, CancellationToken cancellationToken)
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
        if (!columns.ContainsKey(Name) || !columns.ContainsKey(Phone))
        {
            return AppError.Invalid("File", $"The columns {Name} and {Phone} are required.");
        }

        if (content.Rows.Count == 0)
        {
            return AppError.Invalid("File", "The file has no rows.");
        }

        var formulaErrors = content.Rows.Where(r => r.HasFormula).Select(r => new CustomerImportError(r.Number, [], "Formulas are not accepted.")).ToList();
        if (formulaErrors.Count > 0)
        {
            return new CustomerImportResult(0, formulaErrors);
        }

        var requests = new List<(int Row, SaveCustomerRequest Request)>();
        var unreadable = new List<CustomerImportError>();
        foreach (var row in content.Rows)
        {
            string? Cell(string column) => columns.TryGetValue(column, out var index) && index < row.Cells.Count ? NullIfEmpty(row.Cells[index]) : null;
            var invalid = new List<string>();
            CustomerType? type = null;
            if (Cell(Type) is { } typeText)
            {
                if (Types.TryGetValue(typeText, out var t))
                {
                    type = t;
                }
                else
                {
                    invalid.Add(Type);
                }
            }

            Domain.Enums.Gender? gender = null;
            if (Cell(Gender) is { } genderText)
            {
                if (Genders.TryGetValue(genderText, out var g))
                {
                    gender = g;
                }
                else
                {
                    invalid.Add(Gender);
                }
            }

            DateOnly? birth = null;
            if (Cell(BirthDate) is { } birthText)
            {
                if (DateOnly.TryParseExact(birthText, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    birth = date;
                }
                else
                {
                    invalid.Add(BirthDate);
                }
            }

            string? bankBin = null;
            if (Cell(Bank) is { } bankText)
            {
                bankBin = BankCatalog.All.FirstOrDefault(b => string.Equals(b.ShortName, bankText, StringComparison.OrdinalIgnoreCase) || b.Bin == bankText)?.Bin;
                if (bankBin is null)
                {
                    invalid.Add(Bank);
                }
            }

            if (invalid.Count > 0)
            {
                unreadable.Add(new CustomerImportError(row.Number, invalid, "Missing or invalid."));
                continue;
            }

            requests.Add((row.Number, new SaveCustomerRequest(
                Cell(Name), RestoredPhone(Cell(Phone)), Cell(Address), Cell(Note), null, Cell(Code), type, gender, birth, RestoredPhone(Cell(Phone2)), Cell(Email), Cell(Facebook),
                Cell(Province), Cell(Ward), Cell(Company), Cell(TaxCode), null, bankBin, Cell(BankAccount))));
        }

        // Nothing is saved while a row cannot be read; the other rows are checked once these are fixed.
        return unreadable.Count > 0 ? new CustomerImportResult(0, unreadable) : await customers.ImportRowsAsync(requests, cancellationToken);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
