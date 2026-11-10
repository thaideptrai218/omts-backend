using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Identity;
using C = OMTS.Application.Features.Employees.EmployeeFileColumns;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// Imports the rows of an employee file (LABOR-01), Cooperative Owner only. Each row is checked with the rules of a new
/// employee (<see cref="EmployeeProfileValidator"/>, the same as adding one on the screen); rows with an error are skipped and
/// reported, the others are added in one save. The zones come by code, the job types by name.
/// </summary>
public sealed class EmployeeImportService(
    IEmployeeRepository employees,
    IFarmingZoneRepository zones,
    IJobTypeRepository jobTypes,
    ICooperativeRepository cooperatives,
    EmployeeCallerAccess callers,
    EmployeeProfileValidator validator,
    EmployeeCodeAllocator codes,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private const string ImportReason = "Imported from a file.";

    public async Task<Result<EmployeeImportResult>> ImportAsync(IReadOnlyList<EmployeeFileRow> rows, CancellationToken cancellationToken)
    {
        var caller = await callers.GetAsync(cancellationToken);
        if (!caller.IsOwner)
        {
            return AppError.Forbidden;
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        var lookups = new Lookups(zones, jobTypes);
        // Owner rights: every zone is allowed.
        var importer = new EmployeeCaller(true, PermittedZones.Everywhere);
        var errors = new List<EmployeeImportError>();
        var valid = new List<(int Row, EmployeeInput Input)>();
        var phonesInFile = new HashSet<string>();
        var codesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.HasFormula)
            {
                errors.Add(new EmployeeImportError(row.Number, [], "Formulas are not accepted."));
                continue;
            }

            var (request, fileInvalid) = await RequestOfAsync(row, lookups, cancellationToken);
            var checkedInput = await validator.ValidateAsync(request, null, importer, cancellationToken);
            if (!checkedInput.IsSuccess)
            {
                errors.Add(ErrorOf(row.Number, fileInvalid, checkedInput.Error!));
            }
            else if (fileInvalid.Count > 0)
            {
                errors.Add(new EmployeeImportError(row.Number, fileInvalid, UserMessages.Invalid(fileInvalid[0]).Text, UserMessages.Invalid(fileInvalid[0]).Code));
            }
            else if (checkedInput.Value!.PhoneNumber is { } phone && !phonesInFile.Add(phone))
            {
                errors.Add(new EmployeeImportError(row.Number, ["Phone number"], "Repeated in this file."));
            }
            else if (checkedInput.Value.Code is { } code && !codesInFile.Add(code))
            {
                errors.Add(new EmployeeImportError(row.Number, ["Code"], "Repeated in this file."));
            }
            else
            {
                valid.Add((row.Number, checkedInput.Value));
            }
        }

        if (valid.Count > 0 && await SaveAsync(valid, cooperative.Id, codesInFile, cancellationToken) is { } failed)
        {
            return failed;
        }

        return new EmployeeImportResult(valid.Count, errors.OrderBy(e => e.Row).ToList());
    }

    private async Task<AppError?> SaveAsync(List<(int Row, EmployeeInput Input)> valid, Guid cooperativeId, ISet<string> codesInFile, CancellationToken cancellationToken)
    {
        foreach (var (_, input) in valid)
        {
            var employee = EmployeeProfileChanges.AddNew(employees, cooperativeId, input);
            employee.Code ??= await codes.NextAsync(codesInFile, cancellationToken);
            audit.Record(new AuditEntry("Employee.Created", nameof(Employee))
            {
                RecordId = employee.Id,
                After = EmployeeProfileChanges.Snapshot(employee, input.WorkingZoneIds, input.JobTypeIds),
                Reason = ImportReason,
            });
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            // A parallel save took a phone number or a code after the check: nothing of this file is saved.
            return AppError.Conflict(UserMessages.AlreadyExists("Employee"));
        }
    }

    /// <summary>The request of a row, and the fields the file itself got wrong (a date, a gender, a zone code or a job type name that does not exist).</summary>
    private static async Task<(SaveEmployeeRequest Request, List<string> Invalid)> RequestOfAsync(EmployeeFileRow row, Lookups lookups, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        var birthday = C.ParseDate(row.Cell(C.Birthday), out var birthdayValid);
        var startDate = C.ParseDate(row.Cell(C.StartDate), out var startValid);
        var gender = C.ParseGender(row.Cell(C.Gender), out var genderValid);
        AddIf(!birthdayValid, "Birthday");
        AddIf(!startValid, "Start date");
        AddIf(!genderValid, "Gender");

        var zoneIds = new List<Guid>();
        foreach (var code in C.Split(row.Cell(C.Zones)))
        {
            if (await lookups.ZoneIdAsync(code, cancellationToken) is { } id)
            {
                zoneIds.Add(id);
            }
            else
            {
                AddIf(true, "Working zones");
            }
        }

        var jobTypeIds = new List<Guid>();
        foreach (var name in C.Split(row.Cell(C.JobTypes)))
        {
            if (await lookups.JobTypeIdAsync(name, cancellationToken) is { } id)
            {
                jobTypeIds.Add(id);
            }
            else
            {
                AddIf(true, "Job types");
            }
        }

        // An empty cell is "not given" (null), and in a new profile that is the same as empty.
        var bank = new EmployeeBankAccountInput(row.Cell(C.Bank), row.Cell(C.AccountNumber), row.Cell(C.AccountHolder));
        var request = new SaveEmployeeRequest(
            row.Cell(C.Name), C.RestoredPhone(row.Cell(C.Phone)), birthday, row.Cell(C.Address), row.Cell(C.Area), row.Cell(C.Ward), null, row.Cell(C.Notes),
            zoneIds.Distinct().ToList(), bank, C.ParseType(row.Cell(C.Type)), null, jobTypeIds.Distinct().ToList(),
            Code: row.Cell(C.Code), Gender: gender, IdNumber: row.Cell(C.IdNumber), StartDate: startDate);
        return (request, invalid.Distinct().ToList());

        void AddIf(bool condition, string field)
        {
            if (condition)
            {
                invalid.Add(field);
            }
        }
    }

    /// <summary>The refusal of the validator as a row error, with the fields the file got wrong added.</summary>
    private static EmployeeImportError ErrorOf(int row, List<string> fileInvalid, AppError error)
    {
        var fields = new List<string>(fileInvalid);
        if (error.Details?.GetValueOrDefault("fields") is IEnumerable<string> many)
        {
            fields.AddRange(many);
        }
        else if (error.Details?.GetValueOrDefault("field") is string one)
        {
            fields.Add(one);
        }
        else if (error.Message.Code == UserMessages.PhoneBelongsToEmployee(string.Empty).Code)
        {
            // MSG-LABOR-03 names the other employee, not a field.
            fields.Add("Phone number");
        }

        // A zone or job type the file names that does not exist is left out of the request: the validator may then be satisfied with
        // the rest, but the row stays wrong, and the fields say why.
        return new EmployeeImportError(row, fields.Distinct().ToList(), error.Message.Text, error.Message.Code);
    }

    /// <summary>Zones by code and job types by name, each asked once per file.</summary>
    private sealed class Lookups(IFarmingZoneRepository zones, IJobTypeRepository jobTypes)
    {
        private readonly Dictionary<string, Guid?> zoneIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Guid?> jobTypeIds = new(StringComparer.OrdinalIgnoreCase);

        public async Task<Guid?> ZoneIdAsync(string code, CancellationToken cancellationToken)
        {
            if (!zoneIds.TryGetValue(code, out var id))
            {
                zoneIds[code] = id = (await zones.FindByCodeAsync(code, null, cancellationToken))?.Id;
            }

            return id;
        }

        public async Task<Guid?> JobTypeIdAsync(string name, CancellationToken cancellationToken)
        {
            if (!jobTypeIds.TryGetValue(name, out var id))
            {
                jobTypeIds[name] = id = (await jobTypes.FindByNameAsync(name, null, cancellationToken))?.Id;
            }

            return id;
        }
    }
}
