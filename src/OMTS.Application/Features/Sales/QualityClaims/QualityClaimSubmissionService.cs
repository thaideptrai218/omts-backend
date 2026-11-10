using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>
/// CLAIM-01 – a trader reports a quality problem on an invoice of their own customer profile (BR-ACCOUNT-04, BR-CLAIM-01). Everything the save decides is read again after the uploads (GFR-01), and the
/// filtered unique index holds when two tries meet. The profile row is written too (<see cref="ICustomerReferences"/>): a merge at this moment conflicts, as for a purchase request.
/// </summary>
public sealed class QualityClaimSubmissionService(
    ICustomerRepository customers,
    ISalesInvoiceRepository invoices,
    IQualityClaimRepository claims,
    IInvoiceSalesReturnCheck salesReturns,
    IDocumentNumberAllocator numbers,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    QualityClaimNotifications notifications,
    QualityClaimViews views,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string PhotosField = "Photos";

    private sealed record ClaimableInvoice(Customer Customer, SalesInvoice Invoice);

    /// <summary>
    /// 404 for an invoice of another trader; MSG-GEN-09 for one that is cancelled, has a sales return or already has a claim Awaiting response; MSG-GEN-01 for a missing description or photo;
    /// 502 MSG-GEN-03 when a photo cannot be uploaded (nothing is saved).
    /// </summary>
    public async Task<Result<QualityClaimSaved>> SubmitAsync(Guid accountId, Guid invoiceId, SubmitQualityClaim entry, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken)
    {
        var (_, refused) = await LoadClaimableAsync(accountId, invoiceId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var description = PurchaseRequestRules.CleanText(entry.Description);
        var (files, photoHint) = ReadPhotos(photos, entry.OtherFilePart);
        var invalid = new List<string>();
        if (description is null || description.Length > QualityClaim.MaxDescriptionLength)
        {
            invalid.Add("Description");
        }

        if (photoHint is not null)
        {
            invalid.Add(PhotosField);
        }

        if (invalid.Count > 0)
        {
            return invalid is [PhotosField] ? AppError.Invalid(PhotosField, photoHint) : AppError.InvalidFields(invalid);
        }

        var uploaded = await photoStore.UploadAllAsync(FileCategories.QualityClaimPhotos, files, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        return await SaveAsync(accountId, invoiceId, description!, uploaded.Value!, cancellationToken);
    }

    private async Task<Result<QualityClaimSaved>> SaveAsync(Guid accountId, Guid invoiceId, string description, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var prepared = await photoStore.PrepareSaveAsync(keys, () => PrepareAsync(accountId, invoiceId, description, keys, cancellationToken));
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        var claim = prepared.Value!.Claim;
        try
        {
            await fileSaver.SaveAsync(keys, [], cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // The filtered unique index: another claim of this invoice was saved first (BR-CLAIM-01).
            return QualityClaimRules.InvoiceHasAwaitingClaim(prepared.Value.InvoiceCode);
        }

        var saved = UserMessages.Saved(QualityClaimRules.ObjectName);
        var detail = await claims.GetDetailAsync(claim.Id, cancellationToken);
        return new QualityClaimSaved(await views.ForTraderAsync(detail!, cancellationToken), saved.Text, saved.Code);
    }

    private sealed record PreparedClaim(QualityClaim Claim, string InvoiceCode);

    private async Task<Result<PreparedClaim>> PrepareAsync(Guid accountId, Guid invoiceId, string description, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var (claimable, refused) = await LoadClaimableAsync(accountId, invoiceId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var (customer, invoice) = (claimable!.Customer, claimable.Invoice);
        var claim = new QualityClaim
        {
            Code = SalesDocumentCodes.ClaimCode(await numbers.NextAsync(DocumentSequenceKeys.QualityClaim, cancellationToken)),
            InvoiceId = invoice.Id,
            CustomerId = customer.Id,
            SentAt = timeProvider.GetUtcNow(),
            Description = description,
            Photos = keys.Select((key, index) => new QualityClaimPhoto { SortOrder = index + 1, FileKey = key }).ToList(),
        };
        claims.Add(claim);
        unitOfWork.MarkChanged(customer);
        audit.Record(new AuditEntry("QualityClaim.Submitted", QualityClaimRules.RecordType)
        {
            RecordId = claim.Id,
            RecordCode = claim.Code,
            ZoneId = invoice.ZoneId,
            ActorAccountId = accountId,
            After = new { Status = statusCatalog.CodeOf(claim.StatusId), claim.InvoiceId, InvoiceCode = invoice.Code, claim.CustomerId, claim.Description, PhotoCount = keys.Count },
        });
        await notifications.NewClaimAsync(claim, invoice.Code, customer.Name, cancellationToken);
        return new PreparedClaim(claim, invoice.Code);
    }

    private async Task<(ClaimableInvoice? Claimable, AppError? Refused)> LoadClaimableAsync(Guid accountId, Guid invoiceId, CancellationToken cancellationToken)
    {
        if (await customers.GetByAccountIdAsync(accountId, cancellationToken) is not { } customer)
        {
            return (null, AppError.Forbidden);
        }

        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null || invoice.CustomerId != customer.Id)
        {
            return (null, AppError.NotFound);
        }

        if (invoice.StatusId != StatusIds.SalesInvoice.Completed)
        {
            return (null, AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", statusCatalog.DisplayNameOf(invoice.StatusId))));
        }

        if (await salesReturns.HasSalesReturnAsync(invoice.Id, cancellationToken))
        {
            return (null, AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", "returned")));
        }

        return await claims.HasAwaitingAsync(invoice.Id, cancellationToken)
            ? (null, QualityClaimRules.InvoiceHasAwaitingClaim(invoice.Code))
            : (new ClaimableInvoice(customer, invoice), null);
    }

    // CLAIM-01 step 4: at least one photo, JPEG or PNG of at most 10 MB each, at most 10.
    private static (IReadOnlyList<ValidatedFile> Files, string? Hint) ReadPhotos(IReadOnlyList<IncomingFile> photos, bool otherFilePart)
    {
        if (otherFilePart)
        {
            return ([], "Send each photo in a part named photos.");
        }

        if (photos.Count == 0)
        {
            return ([], "At least one photo is required.");
        }

        if (photos.Count > QualityClaim.MaxPhotos)
        {
            return ([], $"A claim holds at most {QualityClaim.MaxPhotos} photos.");
        }

        var validated = FileValidator.Validate(photos, FileRules.Photos);
        return validated.IsValid ? (validated.Files, null) : ([], validated.Problem);
    }
}
