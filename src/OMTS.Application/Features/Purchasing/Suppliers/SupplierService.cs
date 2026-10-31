using OMTS.Application.Common;
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
public sealed class SupplierService(
    ISupplierRepository suppliers,
    IPurchaseReceiptRepository receipts,
    ZoneAccessGuard zoneAccess,
    IFileStorage storage,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    /// <summary>Receipts listed on the supplier detail (newest first).</summary>
    public const int ReceiptHistoryLimit = 50;

    private const string RecordType = nameof(Supplier);
    private const int MaxCertificates = 10;
    private const int MaxAddressLength = 500;

    public async Task<Paged<SupplierListItem>> SearchAsync(
        string? search, SupplierType? type, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Max(page, 1), Math.Clamp(pageSize, 1, 100));
        var (items, total) = await suppliers.SearchAsync(search, type, statusId, page, pageSize, cancellationToken);
        return new Paged<SupplierListItem>(
            items.Select(s => new SupplierListItem(s.Id, s.Name, s.SupplierType, s.PhoneNumber, s.StatusId, statusCatalog.DisplayNameOf(s.StatusId))).ToList(),
            page, pageSize, total);
    }

    public async Task<Result<SupplierView>> GetAsync(Guid supplierId, CancellationToken cancellationToken) =>
        await suppliers.GetForEditAsync(supplierId, cancellationToken) is { } supplier ? await ViewAsync(supplier, cancellationToken) : AppError.NotFound;

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

        var supplier = new Supplier();
        Apply(supplier, values);
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

        if (await rowVersions.CheckAsync(supplier, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(supplier);
        Apply(supplier, values);
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
        return await ViewAsync(supplier, cancellationToken);
    }

    /// <summary>MSG-GEN-09: a Discontinued supplier is not edited (PURCHASE-07.1); it is resumed first.</summary>
    private AppError? NotInUse(Supplier supplier) =>
        supplier.StatusId == StatusIds.Usage.InUse
            ? null
            : AppError.Conflict(UserMessages.WrongStatus("Supplier", statusCatalog.DisplayNameOf(supplier.StatusId)));

    private async Task<SupplierView> ViewAsync(Supplier supplier, CancellationToken cancellationToken)
    {
        // Receipt history only from zones whose receipts the caller may view (receipts still carry a zone until PURCHASE-01
        // is aligned). The supplier debt is PURCHASE-04: not shown here.
        var visible = await zoneAccess.PermittedZonesAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.View, cancellationToken);
        // The latest receipts only (figures summed by SQL), so the detail does not slow down as history grows.
        var history = visible.IsEmpty ? [] : await receipts.ListFiguresAsync(new PurchaseReceiptFilter(visible, SupplierId: supplier.Id), ReceiptHistoryLimit, cancellationToken);
        var certificates = new List<SupplierCertificate>();
        for (var i = 0; i < supplier.CertificateUrls.Count; i++)
        {
            certificates.Add(new SupplierCertificate(i, await storage.GetReadUrlAsync(supplier.CertificateUrls[i], cancellationToken: cancellationToken)));
        }

        return new SupplierView(
            supplier.Id, supplier.Name, supplier.SupplierType, supplier.PhoneNumber, supplier.Address, supplier.Email, certificates,
            supplier.StatusId, statusCatalog.DisplayNameOf(supplier.StatusId),
            history.Select(r => new SupplierReceiptRow(
                r.Id, r.Code, PurchaseMoney.DateOrNull(r.ReceiptDate), r.ZoneId, statusCatalog.DisplayNameOf(r.StatusId), r.Payable, r.Outstanding)).ToList(),
            RowVersionGuard.Encode(supplier.RowVersion));
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

    // AuditEvent.RecordCode holds 100 characters; supplier names may have 200.
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

        return (new Values(name, request.SupplierType ?? default, phone ?? string.Empty, address, email), invalid);
    }

    private static void Apply(Supplier supplier, Values values)
    {
        supplier.Name = values.Name;
        supplier.SupplierType = values.SupplierType;
        supplier.PhoneNumber = values.PhoneNumber;
        supplier.Address = values.Address;
        supplier.Email = values.Email;
    }

    private static object Snapshot(Supplier s) => new { s.Name, s.SupplierType, s.PhoneNumber, s.Address, s.Email };

    private sealed record Values(string Name, SupplierType SupplierType, string PhoneNumber, string? Address, string? Email);
}
