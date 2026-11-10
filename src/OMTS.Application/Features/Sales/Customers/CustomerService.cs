using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>Which money figures of the customer list the caller may read: the debt and the sales follow CUSTOMER_DEBTS View (BR-FARM-07).</summary>
public sealed record CustomerFigureAccess(bool Debt);

/// <summary>
/// SALE-04 – customer profiles of the Zone Manager, the Accountant and the Owner (API filter): no zone (BR-SALE-04). When staff save a profile no other one may have its phone
/// number; the profiles that registration makes are not checked, and are merged later. The phone number of a linked profile follows the account. A profile that has been
/// used (an invoice, a payment, a request, an account) is discontinued and resumed, never deleted; an unused one may be deleted. Only an In Use profile is edited.
/// <see cref="CreateAsync"/> is also the quick create of SALE-05.1. A walk-in customer has no profile, so nothing here stands for one (BR-SALE-07).
/// </summary>
public sealed class CustomerService(
    ICustomerRepository customers,
    ICustomerListQuery list,
    ICustomerReferences references,
    ICooperativeRepository cooperatives,
    CustomerCodeService codes,
    CustomerDebtReader debts,
    IAccountRepository accounts,
    ZoneAccessGuard zoneAccess,
    IFileStorage storage,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    internal const string ObjectName = "Customer profile";
    internal const string MergedStatus = "Merged";
    internal const string RecordType = nameof(Customer);
    public const int MaxBulkIds = 100;
    private const int MaxAuditCodeLength = 100;
    private const int MaxPageSize = 100;
    private const string PhotoCategory = "customer-photos";

    /// <summary>The debt and the sales follow CUSTOMER_DEBTS View: the Zone Manager does not read them.</summary>
    public async Task<CustomerFigureAccess> FigureAccessAsync(CancellationToken cancellationToken) =>
        new(!(await zoneAccess.PermittedZonesAsync(PermissionFunctions.CustomerDebts, PermissionAction.View, cancellationToken)).IsEmpty);

    /// <summary>
    /// SALE-04 step 2 and the customer screen: by name, code or phone number, with the filters of <see cref="CustomerListQuery"/>. A search that reads as a whole number is also
    /// looked up as stored, so <c>+84…</c>, spaces and dots find it. Longer than any name, it finds nothing.
    /// </summary>
    public async Task<Result<CustomerListPage>> SearchAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // The upper bound keeps (page - 1) * pageSize inside int.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        if (filter.Value!.Filter is null)
        {
            return new CustomerListPage([], page, pageSize, 0, filter.Value.Access.Debt ? new CustomerListSummary(0m, 0m, 0m) : null);
        }

        var (rows, total, sums) = await list.SearchAsync(filter.Value.Filter, page, pageSize, cancellationToken);
        return new CustomerListPage(
            await ItemsAsync(rows, filter.Value.Access, cancellationToken), page, pageSize, total,
            filter.Value.Access.Debt ? new CustomerListSummary(sums.Debt, sums.TotalSales, sums.NetSales) : null);
    }

    /// <summary>Every matching profile for the export, with the same permission rules as the list.</summary>
    internal async Task<Result<(IReadOnlyList<CustomerListItem> Items, CustomerFigureAccess Access)>> ExportRowsAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        if (filter.Value!.Filter is null)
        {
            return ((IReadOnlyList<CustomerListItem>)[], filter.Value.Access);
        }

        var rows = await list.ListAsync(filter.Value.Filter, cancellationToken);
        return (await ItemsAsync(rows, filter.Value.Access, cancellationToken, withPhotos: false), filter.Value.Access);
    }

    public async Task<Result<CustomerView>> GetAsync(Guid customerId, CancellationToken cancellationToken) =>
        await customers.GetForEditAsync(customerId, cancellationToken) is { } customer
            ? await ViewAsync(customer, cancellationToken)
            : AppError.NotFound;

    public async Task<Result<CustomerSaved>> CreateAsync(SaveCustomerRequest request, CancellationToken cancellationToken)
    {
        var (values, invalid) = CustomerEntries.From(request, phoneLocked: false, timeProvider.Today());
        if (!CustomerEntries.CodeAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await DuplicateAsync(values.PhoneNumber!, null, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        var (code, codeError) = await codes.ForNewAsync(request.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        var customer = new Customer { CooperativeId = cooperative.Id, Code = code! };
        Apply(customer, values);
        customers.Add(customer);
        audit.Record(new AuditEntry("Customer.Created", RecordType) { RecordId = customer.Id, RecordCode = AuditCode(customer), After = Snapshot(customer) });
        if (await SaveAsync(values.PhoneNumber!, null, customer.Code, customer.Id, cancellationToken) is { } error)
        {
            return error;
        }

        return await SavedAsync(customer, cancellationToken);
    }

    /// <summary>SALE-04.2: invoices stay with the profile; a merged or discontinued profile is read-only.</summary>
    public async Task<Result<CustomerSaved>> UpdateAsync(Guid customerId, SaveCustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = await customers.GetForEditAsync(customerId, cancellationToken);
        if (customer is null)
        {
            return AppError.NotFound;
        }

        if (NotEditable(customer) is { } refused)
        {
            return refused;
        }

        // First (NFR-09): a form read before the trader changed the number holds the old one, and is stale, not a change.
        if (await rowVersions.CheckAsync(customer, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var locked = customer.AccountId is not null;
        if (locked && !string.IsNullOrWhiteSpace(request.PhoneNumber) && PhoneNumbers.Normalize(request.PhoneNumber) != customer.PhoneNumber)
        {
            return AppError.Invalid("Phone number", "The phone number of a profile linked to a trader account is changed only by the trader.");
        }

        var (values, invalid) = CustomerEntries.From(request, locked, timeProvider.Today());
        if (invalid.Count > 0 || !CustomerEntries.CodeAllowed(request.Code))
        {
            if (!CustomerEntries.CodeAllowed(request.Code))
            {
                invalid.Add("Code");
            }

            return AppError.InvalidFields(invalid);
        }

        if (!locked && await DuplicateAsync(values.PhoneNumber!, customer.Id, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        var (code, codeError) = await codes.ForEditAsync(request.Code, customer.Id, customer.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var before = Snapshot(customer);
        Apply(customer, values);
        customer.Code = code!;
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(customer));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Customer.Updated", RecordType) { RecordId = customer.Id, RecordCode = AuditCode(customer), Before = changedBefore, After = changedAfter });
            if (await SaveAsync(values.PhoneNumber, customer.Id, customer.Code, customer.Id, cancellationToken) is { } error)
            {
                return error;
            }
        }

        return await SavedAsync(customer, cancellationToken);
    }

    /// <summary>
    /// Deletes a profile nothing refers to (no invoice, payment, debt adjustment, purchase request, sale awaiting its transfer, merged profile or trader account). Otherwise
    /// MSG-GEN-06 names what refers to it, with <c>canDiscontinue</c> so the client can offer to stop it instead.
    /// </summary>
    public async Task<Result<Done>> DeleteAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customers.GetForEditAsync(customerId, cancellationToken);
        if (customer is null)
        {
            return AppError.NotFound;
        }

        if (customer.MergedIntoCustomerId is not null)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {customer.Name}", MergedStatus));
        }

        if (await UsedByAsync(customer, cancellationToken) is { Count: > 0 } used)
        {
            return StillUsed(used);
        }

        var before = Snapshot(customer);
        customers.Remove(customer);
        audit.Record(new AuditEntry("Customer.Deleted", RecordType) { RecordId = customer.Id, RecordCode = AuditCode(customer), Before = before });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ForeignKeyViolationException)
        {
            // A row that names the profile was saved after the check.
            unitOfWork.DiscardChanges();
            var reread = await customers.GetForEditAsync(customerId, cancellationToken);
            return reread is not null && await UsedByAsync(reread, cancellationToken) is { Count: > 0 } now ? StillUsed(now) : throw new InvalidOperationException("A customer could not be deleted.");
        }

        if (customer.PhotoUrl is { } photo)
        {
            await storage.DeleteAsync(photo, cancellationToken);
        }

        return Done.Value;
    }

    /// <summary>Never blocked by what the profile has done: its invoices, payments and debt stay unchanged. A discontinued profile is not offered on a new invoice.</summary>
    public Task<Result<CustomerView>> DiscontinueAsync(Guid customerId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(customerId, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, "Customer.Discontinued", cancellationToken);

    public Task<Result<CustomerView>> ResumeAsync(Guid customerId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(customerId, StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, "Customer.Resumed", cancellationToken);

    /// <summary>Each id on its own, so one refused row does not stop the others; a save that conflicts ends the batch and reports the rest as not processed.</summary>
    public async Task<Result<CustomerBulkResult>> BulkAsync(CustomerBulkRequest request, CancellationToken cancellationToken)
    {
        if (request.Action is not { } action || !Enum.IsDefined(action))
        {
            return AppError.Invalid("Action");
        }

        var ids = request.Ids?.Distinct().ToList() ?? [];
        if (ids.Count is 0 or > MaxBulkIds)
        {
            return AppError.Invalid("Ids", $"Between 1 and {MaxBulkIds} customers.");
        }

        var outcomes = new List<CustomerBulkOutcome>();
        foreach (var id in ids)
        {
            try
            {
                var changed = action switch
                {
                    CustomerBulkAction.Discontinue => (await DiscontinueAsync(id, cancellationToken)).Error,
                    CustomerBulkAction.Resume => (await ResumeAsync(id, cancellationToken)).Error,
                    _ => (await DeleteAsync(id, cancellationToken)).Error,
                };
                outcomes.Add(changed is null ? new CustomerBulkOutcome(id, true, null, null) : new CustomerBulkOutcome(id, false, changed.Message.Code, changed.Message.Text));
            }
            catch (ConcurrencyConflictException)
            {
                unitOfWork.DiscardChanges();
                var stale = UserMessages.ConcurrentUpdateByAnotherUser;
                outcomes.AddRange(ids.Skip(outcomes.Count).Select(rest => new CustomerBulkOutcome(rest, false, stale.Code, stale.Text)));
                break;
            }
        }

        return new CustomerBulkResult(outcomes.Count(o => o.Succeeded), outcomes.Count(o => !o.Succeeded), outcomes);
    }

    /// <summary>The rows of one file, all or nothing: every row is checked first and the profiles go in one save.</summary>
    public async Task<Result<CustomerImportResult>> ImportRowsAsync(IReadOnlyList<(int Row, SaveCustomerRequest Request)> rows, CancellationToken cancellationToken)
    {
        var errors = new List<CustomerImportError>();
        var valid = new List<(int Row, SaveCustomerRequest Request, CustomerEntries Values)>();
        var phonesInFile = new HashSet<string>();
        var codesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var today = timeProvider.Today();
        foreach (var (row, request) in rows)
        {
            var (values, invalid) = CustomerEntries.From(request, phoneLocked: false, today);
            if (!CustomerEntries.CodeAllowed(request.Code))
            {
                invalid.Add("Code");
            }

            if (invalid.Count > 0)
            {
                errors.Add(new CustomerImportError(row, invalid, "Missing or invalid."));
            }
            else if (!phonesInFile.Add(values.PhoneNumber!))
            {
                errors.Add(new CustomerImportError(row, ["Phone number"], "Repeated in this file."));
            }
            else if (DocumentCodes.Typed(request.Code) is { } typed && !codesInFile.Add(typed))
            {
                errors.Add(new CustomerImportError(row, ["Code"], "Repeated in this file."));
            }
            else
            {
                valid.Add((row, request, values));
            }
        }

        var existing = await customers.ExistingPhonesAsync(valid.Select(v => v.Values.PhoneNumber!).ToList(), cancellationToken);
        errors.AddRange(valid.Where(v => existing.Contains(v.Values.PhoneNumber!)).Select(v => new CustomerImportError(v.Row, ["Phone number"], "Already belongs to a customer.")));
        foreach (var (row, request, _) in valid)
        {
            if (DocumentCodes.Typed(request.Code) is { } typed && await customers.FindByCodeAsync(typed, null, cancellationToken) is not null)
            {
                errors.Add(new CustomerImportError(row, ["Code"], "Already belongs to a customer."));
            }
        }

        if (errors.Count > 0)
        {
            return new CustomerImportResult(0, errors.OrderBy(e => e.Row).ToList());
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        foreach (var (_, request, values) in valid)
        {
            var customer = new Customer { CooperativeId = cooperative.Id, Code = DocumentCodes.Typed(request.Code) ?? await codes.NextAsync(cancellationToken) };
            Apply(customer, values);
            customers.Add(customer);
            audit.Record(new AuditEntry("Customer.Created", RecordType) { RecordId = customer.Id, RecordCode = AuditCode(customer), After = Snapshot(customer) });
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel save took a number or a code after the check.
            return AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }

        return new CustomerImportResult(valid.Count, []);
    }

    /// <summary>JPEG or PNG of at most 2 MB, replacing the photo; a failed upload leaves the profile unchanged (MSG-GEN-03).</summary>
    public async Task<Result<CustomerView>> SetPhotoAsync(Guid customerId, IncomingFile? file, CancellationToken cancellationToken)
    {
        var customer = await customers.GetForEditAsync(customerId, cancellationToken);
        if (customer is null)
        {
            return AppError.NotFound;
        }

        if (NotEditable(customer) is { } refused)
        {
            return refused;
        }

        var validated = FileValidator.Validate(file is null ? [] : [file], FileRules.CustomerPhoto);
        if (file is null || !validated.IsValid)
        {
            return AppError.Invalid(validated.InvalidFileName ?? "Photo", validated.Problem);
        }

        var uploaded = await storage.UploadAsync(validated.Files[0], PhotoCategory, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.ServiceUnavailable, UserMessages.UploadFailed with { LegacyCode = MessageCodes.Legacy.Msg04 });
        }

        return await ReplacePhotoAsync(customer, uploaded.Key, cancellationToken);
    }

    public async Task<Result<CustomerView>> RemovePhotoAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customers.GetForEditAsync(customerId, cancellationToken);
        if (customer is null)
        {
            return AppError.NotFound;
        }

        if (NotEditable(customer) is { } refused)
        {
            return refused;
        }

        return customer.PhotoUrl is null ? await ViewAsync(customer, cancellationToken) : await ReplacePhotoAsync(customer, null, cancellationToken);
    }

    private async Task<Result<CustomerView>> ReplacePhotoAsync(Customer customer, string? newKey, CancellationToken cancellationToken)
    {
        var old = customer.PhotoUrl;
        audit.Record(new AuditEntry("Customer.Updated", RecordType)
        {
            RecordId = customer.Id, RecordCode = AuditCode(customer), Before = new { HasPhoto = old is not null }, After = new { HasPhoto = newKey is not null },
        });
        customer.PhotoUrl = newKey;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (old is not null)
        {
            await storage.DeleteAsync(old, cancellationToken);
        }

        return await ViewAsync(customer, cancellationToken);
    }

    private async Task<Result<CustomerView>> ChangeStatusAsync(Guid customerId, int fromStatusId, int toStatusId, string action, CancellationToken cancellationToken)
    {
        var customer = await customers.GetForEditAsync(customerId, cancellationToken);
        if (customer is null)
        {
            return AppError.NotFound;
        }

        if (customer.MergedIntoCustomerId is not null)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {customer.Name}", MergedStatus));
        }

        if (customer.StatusId != fromStatusId)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {customer.Name}", statusCatalog.DisplayNameOf(customer.StatusId)));
        }

        customer.StatusId = toStatusId;
        audit.Record(new AuditEntry(action, RecordType)
        {
            RecordId = customer.Id,
            RecordCode = AuditCode(customer),
            Before = new { Status = statusCatalog.CodeOf(fromStatusId) },
            After = new { Status = statusCatalog.CodeOf(toStatusId) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(customer, cancellationToken);
    }

    /// <summary>MSG-GEN-09: a merged or discontinued profile is not edited; a discontinued one is resumed first.</summary>
    private AppError? NotEditable(Customer customer)
    {
        if (customer.MergedIntoCustomerId is not null)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {customer.Name}", MergedStatus));
        }

        return customer.StatusId == StatusIds.Usage.InUse
            ? null
            : AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {customer.Name}", statusCatalog.DisplayNameOf(customer.StatusId)));
    }

    /// <summary>What refers to the profile, by name, with the number of rows; the trader account counts as one.</summary>
    private async Task<IReadOnlyList<CustomerReferenceCount>> UsedByAsync(Customer customer, CancellationToken cancellationToken)
    {
        var used = (await references.CountAsync(customer.Id, cancellationToken)).Where(c => c.Count > 0).ToList();
        if (customer.AccountId is not null)
        {
            used.Add(new CustomerReferenceCount("Trader account", 1));
        }

        return used;
    }

    private static AppError StillUsed(IReadOnlyList<CustomerReferenceCount> used)
    {
        var related = string.Join(", ", used.Select(u => $"{u.Name} ({u.Count})"));
        return AppError.Conflict(
            UserMessages.StillInUse(used.Count, related),
            new Dictionary<string, object?> { ["canDiscontinue"] = true, ["references"] = used.Select(u => new { u.Name, u.Count }).ToList() });
    }

    /// <summary>MSG-GEN-02 with the id of the profile to open (SALE-04.0.E2).</summary>
    private async Task<AppError?> DuplicateAsync(string phoneNumber, Guid? exceptCustomerId, CancellationToken cancellationToken) =>
        await customers.FindByPhoneAsync(phoneNumber, exceptCustomerId, cancellationToken) is { } same ? AppError.AlreadyExists(ObjectName, same.Id) : null;

    /// <summary>The second of two parallel saves with one number or one code gets the same MSG-GEN-02 as the pre-check.</summary>
    private async Task<AppError?> SaveAsync(string? phoneNumber, Guid? exceptCustomerId, string code, Guid customerId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            if (phoneNumber is not null && await DuplicateAsync(phoneNumber, exceptCustomerId, cancellationToken) is { } duplicate)
            {
                return duplicate;
            }

            if (await customers.FindByCodeAsync(code, customerId, cancellationToken) is { } sameCode)
            {
                return AppError.AlreadyExists("Customer code", sameCode.Id, "Code");
            }

            throw;
        }
    }

    private async Task<Result<CustomerSaved>> SavedAsync(Customer customer, CancellationToken cancellationToken)
    {
        var saved = UserMessages.Saved(ObjectName);
        return new CustomerSaved(await ViewAsync(customer, cancellationToken), saved.Text, saved.Code);
    }

    private static void Apply(Customer customer, CustomerEntries values)
    {
        customer.Name = values.Name;
        customer.Address = values.Address;
        customer.Note = values.Note;
        customer.CustomerType = values.CustomerType;
        customer.Gender = values.Gender;
        customer.BirthDate = values.BirthDate;
        customer.PhoneNumber2 = values.PhoneNumber2;
        customer.Email = values.Email;
        customer.Facebook = values.Facebook;
        customer.Province = values.Province;
        customer.Ward = values.Ward;
        customer.CompanyName = values.CompanyName;
        customer.TaxCode = values.TaxCode;
        customer.IdentityNumber = values.IdentityNumber;
        customer.BankBin = values.BankBin;
        customer.BankName = values.BankName;
        customer.BankAccountNumber = values.BankAccountNumber;
        if (values.PhoneNumber is not null)
        {
            customer.PhoneNumber = values.PhoneNumber;
            customer.PhoneCheckedByStaff = true;
        }
    }

    /// <summary>The profile as the detail shows it, with its debt (the invoice screen shows it to the Zone Manager too).</summary>
    internal async Task<CustomerView> ViewAsync(Customer c, CancellationToken cancellationToken)
    {
        var debt = await debts.DebtAsync(c.Id, cancellationToken);
        var creator = c.CreatedBy is { } createdBy ? (await accounts.GetDisplayNamesAsync([createdBy], cancellationToken)).GetValueOrDefault(createdBy) : null;
        var photo = c.PhotoUrl is { } key ? await storage.GetReadUrlAsync(key, cancellationToken: cancellationToken) : null;
        return new CustomerView(
            c.Id, c.Name, c.PhoneNumber, c.Email, c.Address, c.Note,
            c.AccountId is not null,
            c.Account is { } account ? new LinkedTraderAccount(account.Id, account.Username, account.DisplayName) : null,
            c.MergedIntoCustomerId,
            debt,
            RowVersionGuard.Encode(c.RowVersion),
            c.Code, c.StatusId, statusCatalog.DisplayNameOf(c.StatusId), c.CustomerType, c.Gender, c.BirthDate, c.PhoneNumber2, c.Facebook, c.Province, c.Ward,
            c.CompanyName, c.TaxCode, c.IdentityNumber, c.BankBin, c.BankName, c.BankAccountNumber, photo, c.CreatedAt, creator,
            await UsedByAsync(c, cancellationToken) is { Count: > 0 });
    }

    private async Task<IReadOnlyList<CustomerListItem>> ItemsAsync(
        IReadOnlyList<CustomerFigureRow> rows, CustomerFigureAccess access, CancellationToken cancellationToken, bool withPhotos = true)
    {
        var creators = rows.Select(r => r.Customer.CreatedBy).OfType<Guid>().Distinct().ToList();
        var names = creators.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(creators, cancellationToken);
        var items = new List<CustomerListItem>(rows.Count);
        foreach (var r in rows)
        {
            var c = r.Customer;
            var photo = withPhotos && c.PhotoUrl is { } key ? await storage.GetReadUrlAsync(key, cancellationToken: cancellationToken) : null;
            items.Add(new CustomerListItem(
                c.Id, c.Code, c.Name, c.PhoneNumber, c.Address, c.AccountId is not null, RowVersionGuard.Encode(c.RowVersion), c.StatusId, statusCatalog.DisplayNameOf(c.StatusId),
                c.CustomerType, c.Gender, c.BirthDate, c.PhoneNumber2, c.Email, c.Facebook, c.Province, c.Ward, c.CompanyName, c.TaxCode, c.Note, photo,
                r.LastTransactionAt, c.CreatedAt, c.CreatedBy is { } id ? names.GetValueOrDefault(id) : null,
                access.Debt ? r.Debt : null, access.Debt ? r.TotalSales : null, access.Debt ? r.NetSales : null));
        }

        return items;
    }

    /// <summary>The filter of a query; a null filter means the search text can match nothing.</summary>
    private async Task<Result<(CustomerSearchFilter? Filter, CustomerFigureAccess Access)>> FilterAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        var access = await FigureAccessAsync(cancellationToken);
        var (sort, known) = SortOf(query.SortBy);
        if (!known)
        {
            return AppError.Invalid("Sort by");
        }

        if (query.SortDir is not (null or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction");
        }

        var usesFigures = sort is CustomerSort.TotalSales or CustomerSort.NetSales or CustomerSort.CurrentDebt
            || query.TotalSalesFrom is not null || query.TotalSalesTo is not null || query.SalesFrom is not null || query.SalesTo is not null
            || query.CurrentDebtFrom is not null || query.CurrentDebtTo is not null;
        if (usesFigures && !access.Debt)
        {
            return AppError.Forbidden;
        }

        if (query.TotalSalesFrom > query.TotalSalesTo)
        {
            return AppError.Invalid("Total sales");
        }

        if (query.CurrentDebtFrom > query.CurrentDebtTo)
        {
            return AppError.Invalid("Current debt");
        }

        foreach (var (from, to, field) in new[]
        {
            (query.SalesFrom, query.SalesTo, "Sales period"),
            (query.LastTransactionFrom, query.LastTransactionTo, "Last transaction"),
            (query.CreatedFrom, query.CreatedTo, "Created"),
        })
        {
            if (from > to)
            {
                return AppError.Invalid(field);
            }
        }

        if ((query.BirthdayFrom is null) != (query.BirthdayTo is null))
        {
            return AppError.Invalid("Birthday");
        }

        var text = DisplayNames.Normalize(query.Search);
        if (text.Length > DisplayNames.MaxLength)
        {
            return (null, access);
        }

        int? statusId = query.Status switch
        {
            CustomerStatusFilter.InUse => StatusIds.Usage.InUse,
            CustomerStatusFilter.Discontinued => StatusIds.Usage.Discontinued,
            _ => null,
        };
        var descending = sort == CustomerSort.Code ? query.SortDir != "asc" : query.SortDir == "desc";
        return (new CustomerSearchFilter(
            text.Length == 0 ? null : text, PhoneNumbers.Normalize(text), query.Type, query.Gender, query.BirthdayFrom, query.BirthdayTo,
            query.LastTransactionFrom, query.LastTransactionTo, query.TotalSalesFrom, query.TotalSalesTo, query.SalesFrom, query.SalesTo,
            query.CurrentDebtFrom, query.CurrentDebtTo, query.Province, statusId, query.CreatedById, query.CreatedFrom, query.CreatedTo, sort, descending), access);
    }

    private static (CustomerSort Sort, bool Known) SortOf(string? sortBy) => sortBy switch
    {
        null or "code" => (CustomerSort.Code, true),
        "name" => (CustomerSort.Name, true),
        "phone" => (CustomerSort.Phone, true),
        "type" => (CustomerSort.Type, true),
        "province" => (CustomerSort.Province, true),
        "createdAt" => (CustomerSort.CreatedAt, true),
        "status" => (CustomerSort.Status, true),
        "totalSales" => (CustomerSort.TotalSales, true),
        "netSales" => (CustomerSort.NetSales, true),
        "currentDebt" => (CustomerSort.CurrentDebt, true),
        "lastTransaction" => (CustomerSort.LastTransaction, true),
        _ => (default, false),
    };

    // The identity number is personal data: the audit log only says whether one is held.
    private static object Snapshot(Customer c) => new
    {
        c.Code, c.Name, c.PhoneNumber, c.PhoneNumber2, c.Address, c.Note, c.Email, c.CustomerType, c.Gender, c.BirthDate, c.Facebook, c.Province, c.Ward,
        c.CompanyName, c.TaxCode, HasIdentityNumber = c.IdentityNumber is not null, c.BankBin, c.BankName, c.BankAccountNumber,
    };

    // AuditEvent.RecordCode holds 100 characters; the phone number is the business identity of a profile, when it has one.
    internal static string AuditCode(Customer c) => c.PhoneNumber ?? (c.Name.Length <= MaxAuditCodeLength ? c.Name : c.Name[..MaxAuditCodeLength]);
}
