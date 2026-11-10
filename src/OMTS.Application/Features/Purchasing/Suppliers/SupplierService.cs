using System.Text.RegularExpressions;
using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers;

/// <summary>
/// PURCHASE-07 – one cooperative-wide supplier list with no zone, so it follows the role only (BR-PURCHASE-06,
/// BR-FARM-08): the Zone Manager maintains it, the Accountant views it (API filter). Only an In Use supplier is edited;
/// suppliers are discontinued and resumed, never deleted (GBR-01).
/// </summary>
public sealed partial class SupplierService(
    ISupplierRepository suppliers,
    IPurchaseReceiptRepository receipts,
    IAccountRepository accounts,
    ZoneAccessGuard zoneAccess,
    IFileStorage storage,
    IStatusCatalog statusCatalog,
    IDocumentNumberAllocator numbers,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Receipts listed on the supplier detail (newest first).</summary>
    public const int ReceiptHistoryLimit = 50;

    private const string RecordType = nameof(Supplier);
    private const int MaxCertificates = 10;
    private const int MaxAddressLength = 500;
    private const int MaxBulkIds = 100;

    public async Task<Result<Paged<SupplierListItem>>> SearchAsync(SupplierListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / 100), Math.Clamp(query.PageSize, 1, 100));
        var (items, total) = await suppliers.SearchAsync(filter.Value!.Filter, page, pageSize, cancellationToken);
        return new Paged<SupplierListItem>(await ListItemsAsync(items, filter.Value.Access, cancellationToken), page, pageSize, total);
    }

    /// <summary>Every matching supplier for the export, with the same permission rules as the list.</summary>
    public async Task<Result<(IReadOnlyList<SupplierListItem> Items, SupplierFigureAccess Access)>> ExportRowsAsync(SupplierListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var rows = await suppliers.ListAsync(filter.Value!.Filter, cancellationToken);
        return (await ListItemsAsync(rows, filter.Value.Access, cancellationToken), filter.Value.Access);
    }

    /// <summary>The receipts and returns of the supplier, newest first (the API checks PURCHASE_RECEIPTS View).</summary>
    public async Task<Result<Paged<SupplierHistoryItem>>> HistoryAsync(Guid supplierId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (await suppliers.GetForEditAsync(supplierId, cancellationToken) is null)
        {
            return AppError.NotFound;
        }

        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / 100), Math.Clamp(pageSize, 1, 100));
        var (items, total) = await suppliers.HistoryAsync(supplierId, page, pageSize, cancellationToken);
        var names = await accounts.GetDisplayNamesAsync(items.Select(r => r.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        return new Paged<SupplierHistoryItem>(
            items.Select(r => new SupplierHistoryItem(
                r.Id, r.ReceiptId, r.IsReturn ? "Return" : "Receipt", r.Code, r.Date, r.CreatedAt,
                names.GetValueOrDefault(r.RecordedByAccountId), r.Total, r.StatusId, statusCatalog.DisplayNameOf(r.StatusId))).ToList(),
            page, pageSize, total);
    }

    /// <summary>Each id on its own, so one refused row does not stop the others; a save that conflicts ends the batch and reports the rest as not processed.</summary>
    public async Task<Result<SupplierBulkResult>> BulkStatusAsync(SupplierBulkStatusRequest request, CancellationToken cancellationToken)
    {
        if (request.Action is not { } action || !Enum.IsDefined(action))
        {
            return AppError.Invalid("Action");
        }

        var ids = request.Ids?.Distinct().ToList() ?? [];
        if (ids.Count is 0 or > MaxBulkIds)
        {
            return AppError.Invalid("Ids", $"Between 1 and {MaxBulkIds} suppliers.");
        }

        var outcomes = new List<SupplierBulkOutcome>();
        foreach (var id in ids)
        {
            try
            {
                var changed = action == SupplierBulkAction.Discontinue
                    ? await ApplyStatusAsync(id, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, "Supplier.Discontinued", cancellationToken)
                    : await ApplyStatusAsync(id, StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, "Supplier.Reactivated", cancellationToken);
                outcomes.Add(changed.IsSuccess
                    ? new SupplierBulkOutcome(id, true, null, null)
                    : new SupplierBulkOutcome(id, false, changed.Error!.Message.Code, changed.Error.Message.Text));
            }
            catch (ConcurrencyConflictException)
            {
                var stale = UserMessages.ConcurrentUpdateByAnotherUser;
                outcomes.AddRange(ids.Skip(outcomes.Count).Select(rest => new SupplierBulkOutcome(rest, false, stale.Code, stale.Text)));
                break;
            }
        }

        return new SupplierBulkResult(outcomes.Count(o => o.Succeeded), outcomes.Count(o => !o.Succeeded), outcomes);
    }

    /// <summary>The rows of one file, all or nothing: every row is checked first and the suppliers go in one save.</summary>
    public async Task<Result<SupplierImportResult>> ImportRowsAsync(IReadOnlyList<(int Row, SaveSupplierRequest Request)> rows, CancellationToken cancellationToken)
    {
        var errors = new List<SupplierImportError>();
        var valid = new List<(int Row, Values Values)>();
        var inFile = new HashSet<string>();
        foreach (var (row, request) in rows)
        {
            var (values, invalid) = Validate(request);
            if (invalid.Count > 0)
            {
                errors.Add(new SupplierImportError(row, invalid, "Missing or invalid."));
            }
            else if (!inFile.Add(values.PhoneNumber))
            {
                errors.Add(new SupplierImportError(row, ["Phone number"], "Repeated in this file."));
            }
            else
            {
                valid.Add((row, values));
            }
        }

        var existing = await suppliers.ExistingPhonesAsync(valid.Select(v => v.Values.PhoneNumber).ToList(), cancellationToken);
        errors.AddRange(valid.Where(v => existing.Contains(v.Values.PhoneNumber))
            .Select(v => new SupplierImportError(v.Row, ["Phone number"], "Already belongs to a supplier.")));
        if (errors.Count > 0)
        {
            return new SupplierImportResult(0, errors.OrderBy(e => e.Row).ToList());
        }

        foreach (var (_, values) in valid)
        {
            var supplier = new Supplier { Code = await NextCodeAsync(cancellationToken) };
            Apply(supplier, values, isNew: true);
            suppliers.Add(supplier);
            audit.Record(new AuditEntry("Supplier.Created", RecordType) { RecordId = supplier.Id, RecordCode = AuditCode(supplier), After = Snapshot(supplier) });
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel save took one of the phone numbers after the check.
            return AppError.Conflict(UserMessages.AlreadyExists("Supplier"));
        }

        return new SupplierImportResult(valid.Count, []);
    }

    /// <summary>Total purchased follows the receipts it sums (PURCHASE_RECEIPTS View); debt follows SUPPLIER_PAYMENTS View (BR-FARM-07).</summary>
    public async Task<SupplierFigureAccess> FigureAccessAsync(CancellationToken cancellationToken) => new(
        !(await zoneAccess.PermittedZonesAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.View, cancellationToken)).IsEmpty,
        !(await zoneAccess.PermittedZonesAsync(PermissionFunctions.SupplierPayments, PermissionAction.View, cancellationToken)).IsEmpty);

    public async Task<Result<SupplierView>> GetAsync(Guid supplierId, CancellationToken cancellationToken) =>
        await suppliers.GetForEditAsync(supplierId, cancellationToken) is { } supplier ? await ViewAsync(supplier, cancellationToken) : AppError.NotFound;

    /// <summary>A typed code must have the allowed characters and belong to no other supplier, whatever the letter case.</summary>
    private async Task<AppError?> TypedCodeErrorAsync(string? input, Guid? supplierId, CancellationToken cancellationToken)
    {
        if (!DocumentCodes.TypedAllowed(input))
        {
            return AppError.InvalidFields(["Code"]);
        }

        return DocumentCodes.Typed(input) is { } code && await suppliers.FindByCodeAsync(code, supplierId, cancellationToken) is { } other
            ? AppError.AlreadyExists("Supplier code", other.Id, "Code")
            : null;
    }

    /// <summary>The next generated NCC code; a number whose code someone already typed is spent and skipped, never reused.</summary>
    private async Task<string> NextCodeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = DocumentCodes.SupplierCode(await numbers.NextAsync(DocumentSequenceKeys.Supplier, cancellationToken));
            if (await suppliers.FindByCodeAsync(code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    public async Task<Result<SupplierView>> CreateAsync(SaveSupplierRequest request, CancellationToken cancellationToken)
    {
        var (values, invalid) = Validate(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await suppliers.FindByPhoneAsync(values.PhoneNumber, null, cancellationToken) is { } existing)
        {
            return AppError.AlreadyExists("Supplier", existing.Id);
        }

        if (await TypedCodeErrorAsync(request.Code, null, cancellationToken) is { } codeError)
        {
            return codeError;
        }

        var supplier = new Supplier { Code = DocumentCodes.Typed(request.Code) ?? await NextCodeAsync(cancellationToken) };
        Apply(supplier, values, isNew: true);
        suppliers.Add(supplier);
        audit.Record(new AuditEntry("Supplier.Created", RecordType) { RecordId = supplier.Id, RecordCode = AuditCode(supplier), After = Snapshot(supplier) });
        if (await SaveAsync(values.PhoneNumber, null, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return await ViewAsync(supplier, cancellationToken);
    }

    public async Task<Result<SupplierView>> UpdateAsync(Guid supplierId, SaveSupplierRequest request, CancellationToken cancellationToken)
    {
        var supplier = await suppliers.GetForEditAsync(supplierId, cancellationToken);
        if (supplier is null)
        {
            return AppError.NotFound;
        }

        if (NotInUse(supplier) is { } discontinued)
        {
            return discontinued;
        }

        var (values, invalid) = Validate(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await suppliers.FindByPhoneAsync(values.PhoneNumber, supplier.Id, cancellationToken) is { } existing)
        {
            return AppError.AlreadyExists("Supplier", existing.Id);
        }

        if (await TypedCodeErrorAsync(request.Code, supplier.Id, cancellationToken) is { } codeError)
        {
            return codeError;
        }

        if (await rowVersions.CheckAsync(supplier, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(supplier);
        Apply(supplier, values, isNew: false);
        // A supplier code is only a label (no lot or document derives from it), so it may be changed at any time.
        if (DocumentCodes.Typed(request.Code) is { } typedCode)
        {
            supplier.Code = typedCode;
        }
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(supplier));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Supplier.Updated", RecordType) { RecordId = supplier.Id, RecordCode = AuditCode(supplier), Before = changedBefore, After = changedAfter });
            if (await SaveAsync(values.PhoneNumber, supplier.Id, cancellationToken) is { } duplicate)
            {
                return duplicate;
            }
        }

        return await ViewAsync(supplier, cancellationToken);
    }

    /// <summary>Never blocked: existing receipts, seed lots and payments stay unchanged.</summary>
    public Task<Result<SupplierView>> DiscontinueAsync(Guid supplierId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(supplierId, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, "Supplier.Discontinued", cancellationToken);

    /// <summary>Resume (PURCHASE-07.2): back to In Use, offered again on new receipts.</summary>
    public Task<Result<SupplierView>> ReactivateAsync(Guid supplierId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(supplierId, StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, "Supplier.Reactivated", cancellationToken);

    /// <summary>JPEG, PNG or PDF certificates of an In Use supplier; a failed upload leaves the supplier unchanged (MSG-GEN-03).</summary>
    public async Task<Result<SupplierView>> AddCertificatesAsync(Guid supplierId, IReadOnlyList<IncomingFile> files, CancellationToken cancellationToken)
    {
        var supplier = await suppliers.GetForEditAsync(supplierId, cancellationToken);
        if (supplier is null)
        {
            return AppError.NotFound;
        }

        if (NotInUse(supplier) is { } discontinued)
        {
            return discontinued;
        }

        var validated = FileValidator.Validate(files, FileRules.Documents);
        if (files.Count == 0 || !validated.IsValid)
        {
            return AppError.Invalid(validated.InvalidFileName ?? "Certificates", validated.Problem);
        }

        if (supplier.CertificateUrls.Count + validated.Files.Count > MaxCertificates)
        {
            return AppError.Invalid("Certificates", $"At most {MaxCertificates} certificates can be kept.");
        }

        var keys = new List<string>();
        foreach (var file in validated.Files)
        {
            var uploaded = await storage.UploadAsync(file, "supplier-certificates", cancellationToken);
            if (!uploaded.Succeeded)
            {
                return new AppError(ErrorKind.ServiceUnavailable, UserMessages.UploadFailed with { LegacyCode = MessageCodes.Legacy.Msg04 });
            }

            keys.Add(uploaded.Key!);
        }

        return await SetCertificatesAsync(supplier, [.. supplier.CertificateUrls, .. keys], cancellationToken);
    }

    /// <summary>Removes the reference only (In Use supplier); the stored file is kept.</summary>
    public async Task<Result<SupplierView>> RemoveCertificateAsync(Guid supplierId, int index, CancellationToken cancellationToken)
    {
        var supplier = await suppliers.GetForEditAsync(supplierId, cancellationToken);
        if (supplier is null)
        {
            return AppError.NotFound;
        }

        if (NotInUse(supplier) is { } discontinued)
        {
            return discontinued;
        }

        if (index < 0 || index >= supplier.CertificateUrls.Count)
        {
            return AppError.Invalid("Certificate");
        }

        return await SetCertificatesAsync(supplier, supplier.CertificateUrls.Where((_, i) => i != index).ToList(), cancellationToken);
    }

    private async Task<Result<SupplierView>> SetCertificatesAsync(Supplier supplier, List<string> keys, CancellationToken cancellationToken)
    {
        audit.Record(new AuditEntry("Supplier.Updated", RecordType)
        {
            RecordId = supplier.Id, RecordCode = AuditCode(supplier), Before = new { Certificates = supplier.CertificateUrls }, After = new { Certificates = keys },
        });
        supplier.CertificateUrls = keys;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(supplier, cancellationToken);
    }

    private async Task<Result<SupplierView>> ChangeStatusAsync(
        Guid supplierId, int fromStatusId, int toStatusId, string action, CancellationToken cancellationToken)
    {
        var changed = await ApplyStatusAsync(supplierId, fromStatusId, toStatusId, action, cancellationToken);
        return changed.IsSuccess ? await ViewAsync(changed.Value!, cancellationToken) : changed.Error!;
    }

    private async Task<Result<Supplier>> ApplyStatusAsync(
        Guid supplierId, int fromStatusId, int toStatusId, string action, CancellationToken cancellationToken)
    {
        var supplier = await suppliers.GetForEditAsync(supplierId, cancellationToken);
        if (supplier is null)
        {
            return AppError.NotFound;
        }

        if (supplier.StatusId != fromStatusId)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Supplier", statusCatalog.DisplayNameOf(supplier.StatusId)));
        }

        supplier.StatusId = toStatusId;
        audit.Record(new AuditEntry(action, RecordType)
        {
            RecordId = supplier.Id,
            RecordCode = AuditCode(supplier),
            Before = new { Status = statusCatalog.CodeOf(fromStatusId) },
            After = new { Status = statusCatalog.CodeOf(toStatusId) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return supplier;
    }

    /// <summary>MSG-GEN-09: a Discontinued supplier is not edited (PURCHASE-07.1); it is resumed first.</summary>
    private AppError? NotInUse(Supplier supplier) =>
        supplier.StatusId == StatusIds.Usage.InUse
            ? null
            : AppError.Conflict(UserMessages.WrongStatus("Supplier", statusCatalog.DisplayNameOf(supplier.StatusId)));

    private async Task<SupplierView> ViewAsync(Supplier supplier, CancellationToken cancellationToken)
    {
        // Receipt history for the callers who view purchase receipts (no zone: the role only, BR-FARM-08). The supplier debt
        // is PURCHASE-04: not shown here.
        var visible = await zoneAccess.PermittedZonesAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.View, cancellationToken);
        // The latest receipts only (figures summed by SQL), so the detail does not slow down as history grows.
        var history = visible.IsEmpty ? [] : await receipts.ListFiguresAsync(new PurchaseReceiptFilter(SupplierId: supplier.Id), ReceiptHistoryLimit, cancellationToken);
        var certificates = new List<SupplierCertificate>();
        for (var i = 0; i < supplier.CertificateUrls.Count; i++)
        {
            certificates.Add(new SupplierCertificate(i, await storage.GetReadUrlAsync(supplier.CertificateUrls[i], cancellationToken: cancellationToken)));
        }

        var creator = supplier.CreatedBy is { } createdBy ? (await accounts.GetDisplayNamesAsync([createdBy], cancellationToken)).GetValueOrDefault(createdBy) : null;
        return new SupplierView(
            supplier.Id, supplier.Name, supplier.SupplierType, supplier.PhoneNumber, supplier.Address, supplier.Email, certificates,
            supplier.StatusId, statusCatalog.DisplayNameOf(supplier.StatusId),
            history.Select(r => new SupplierReceiptRow(r.Id, r.Code, r.ReceiptDate, r.StatusId, statusCatalog.DisplayNameOf(r.StatusId), r.Payable)).ToList(),
            RowVersionGuard.Encode(supplier.RowVersion),
            supplier.Code, supplier.Province, supplier.Ward, supplier.Note, supplier.CompanyName, supplier.TaxCode, supplier.IdentityNumber,
            supplier.CreatedAt, creator);
    }

    private async Task<Result<(SupplierSearchFilter Filter, SupplierFigureAccess Access)>> FilterAsync(SupplierListQuery query, CancellationToken cancellationToken)
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

        var usesTotal = sort == SupplierSort.TotalPurchased || query.TotalPurchasedFrom is not null || query.TotalPurchasedTo is not null
            || query.PurchaseFrom is not null || query.PurchaseTo is not null;
        var usesDebt = sort == SupplierSort.CurrentDebt || query.CurrentDebtFrom is not null || query.CurrentDebtTo is not null;
        if ((usesTotal && !access.TotalPurchased) || (usesDebt && !access.CurrentDebt))
        {
            return AppError.Forbidden;
        }

        if (query.TotalPurchasedFrom > query.TotalPurchasedTo)
        {
            return AppError.Invalid("Total purchased");
        }

        if (query.CurrentDebtFrom > query.CurrentDebtTo)
        {
            return AppError.Invalid("Current debt");
        }

        if (query.PurchaseFrom > query.PurchaseTo)
        {
            return AppError.Invalid("Purchase date");
        }

        return (new SupplierSearchFilter(
            query.Search, query.Type, query.StatusId, timeProvider.Today(), query.PurchaseFrom, query.PurchaseTo,
            query.TotalPurchasedFrom, query.TotalPurchasedTo, query.CurrentDebtFrom, query.CurrentDebtTo, sort, query.SortDir == "desc"), access);
    }

    private static (SupplierSort Sort, bool Known) SortOf(string? sortBy) => sortBy switch
    {
        null or "name" => (SupplierSort.Name, true),
        "code" => (SupplierSort.Code, true),
        "phone" => (SupplierSort.Phone, true),
        "type" => (SupplierSort.Type, true),
        "province" => (SupplierSort.Province, true),
        "createdAt" => (SupplierSort.CreatedAt, true),
        "status" => (SupplierSort.Status, true),
        "totalPurchased" => (SupplierSort.TotalPurchased, true),
        "currentDebt" => (SupplierSort.CurrentDebt, true),
        _ => (default, false),
    };

    private async Task<IReadOnlyList<SupplierListItem>> ListItemsAsync(IReadOnlyList<SupplierFigureRow> rows, SupplierFigureAccess access, CancellationToken cancellationToken)
    {
        var creators = rows.Select(r => r.Supplier.CreatedBy).OfType<Guid>().Distinct().ToList();
        var names = creators.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(creators, cancellationToken);
        return rows.Select(r => new SupplierListItem(
            r.Supplier.Id, r.Supplier.Name, r.Supplier.SupplierType, r.Supplier.PhoneNumber, r.Supplier.StatusId, statusCatalog.DisplayNameOf(r.Supplier.StatusId),
            r.Supplier.Code, r.Supplier.Email, r.Supplier.Address, r.Supplier.Province, r.Supplier.Ward, r.Supplier.CompanyName, r.Supplier.TaxCode, r.Supplier.Note,
            r.Supplier.CreatedAt, r.Supplier.CreatedBy is { } id ? names.GetValueOrDefault(id) : null,
            access.TotalPurchased ? r.TotalPurchased : null,
            access.CurrentDebt ? r.CurrentDebt : null)).ToList();
    }

    /// <summary>The second of two parallel saves with one phone gets the same MSG-GEN-02 as the pre-check.</summary>
    private async Task<AppError?> SaveAsync(string phoneNumber, Guid? exceptSupplierId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            if (await suppliers.FindByPhoneAsync(phoneNumber, exceptSupplierId, cancellationToken) is { } existing)
            {
                return AppError.AlreadyExists("Supplier", existing.Id);
            }

            throw;
        }
    }

    // AuditEvent.RecordCode is shorter than a supplier name, which may have 200 characters: cut to 100.
    private static string AuditCode(Supplier supplier) => supplier.Name.Length <= 100 ? supplier.Name : supplier.Name[..100];

    private static (Values Values, List<string> Invalid) Validate(SaveSupplierRequest request)
    {
        var invalid = new List<string>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
        {
            invalid.Add("Name");
        }

        if (request.SupplierType is not { } type || !Enum.IsDefined(type))
        {
            invalid.Add("Supplier type");
        }

        var phone = PhoneNumbers.Normalize(request.PhoneNumber);
        if (phone is null || phone.Length > 20)
        {
            invalid.Add("Phone number");
        }

        var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
        if (address?.Length > MaxAddressLength)
        {
            invalid.Add("Address");
        }

        string? email = null;
        if (!string.IsNullOrWhiteSpace(request.Email) && (email = EmailAddresses.Normalize(request.Email)) is null)
        {
            invalid.Add("Email");
        }

        CheckLength(request.Province, 100, "Province", invalid);
        CheckLength(request.Ward, 100, "Ward", invalid);
        CheckLength(request.Note, 2000, "Note", invalid);
        CheckLength(request.CompanyName, 200, "Company name", invalid);
        if (!string.IsNullOrWhiteSpace(request.TaxCode) && !TaxCodeFormat().IsMatch(request.TaxCode.Trim()))
        {
            invalid.Add("Tax code");
        }

        if (!string.IsNullOrWhiteSpace(request.IdentityNumber) && !IdentityNumberFormat().IsMatch(request.IdentityNumber.Trim()))
        {
            invalid.Add("Identity number");
        }

        return (new Values(name, request.SupplierType ?? default, phone ?? string.Empty, address, email,
            request.Province, request.Ward, request.Note, request.CompanyName, request.TaxCode, request.IdentityNumber), invalid);
    }

    private static void CheckLength(string? value, int max, string field, List<string> invalid)
    {
        if (value?.Trim().Length > max)
        {
            invalid.Add(field);
        }
    }

    private static void Apply(Supplier supplier, Values values, bool isNew)
    {
        supplier.Name = values.Name;
        supplier.SupplierType = values.SupplierType;
        supplier.PhoneNumber = values.PhoneNumber;
        supplier.Address = values.Address;
        supplier.Email = values.Email;
        supplier.Province = Optional(values.Province, supplier.Province, isNew);
        supplier.Ward = Optional(values.Ward, supplier.Ward, isNew);
        supplier.Note = Optional(values.Note, supplier.Note, isNew);
        supplier.CompanyName = Optional(values.CompanyName, supplier.CompanyName, isNew);
        supplier.TaxCode = Optional(values.TaxCode, supplier.TaxCode, isNew);
        supplier.IdentityNumber = Optional(values.IdentityNumber, supplier.IdentityNumber, isNew);
    }

    /// <summary>On an update, null keeps the value (a client that does not know the field) and an empty text clears it.</summary>
    private static string? Optional(string? entered, string? current, bool isNew) =>
        entered is null ? (isNew ? null : current) : string.IsNullOrWhiteSpace(entered) ? null : entered.Trim();

    // The identity number is personal data: the audit log only says whether one is held.
    private static object Snapshot(Supplier s) => new
    {
        s.Code, s.Name, s.SupplierType, s.PhoneNumber, s.Address, s.Email, s.Province, s.Ward, s.Note, s.CompanyName, s.TaxCode,
        HasIdentityNumber = s.IdentityNumber is not null,
    };

    [GeneratedRegex(@"^(\d{10}(-\d{3})?|\d{12})$")]
    private static partial Regex TaxCodeFormat();

    [GeneratedRegex(@"^\d{12}$")]
    private static partial Regex IdentityNumberFormat();

    private sealed record Values(
        string Name, SupplierType SupplierType, string PhoneNumber, string? Address, string? Email,
        string? Province, string? Ward, string? Note, string? CompanyName, string? TaxCode, string? IdentityNumber);
}

/// <summary>Which money figures of the supplier list the caller may read.</summary>
public sealed record SupplierFigureAccess(bool TotalPurchased, bool CurrentDebt);
