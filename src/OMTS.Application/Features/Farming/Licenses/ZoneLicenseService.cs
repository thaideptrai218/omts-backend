using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>
/// FARM-03 – zone licenses (Cooperative Owner only, checked by the API filter): create, view, edit and replace; never
/// deleted. A license belongs to one zone, and is added, edited or replaced only while that zone is In Use (GBR-01,
/// MSG-GEN-09); a Replaced license is kept as it is (MSG-GEN-09). The file is uploaded first, so a storage failure saves
/// nothing; an upload whose database save fails is removed again, and a file replaced by an edit is removed once the edit
/// is saved (<see cref="UploadedFileSaver"/>).
/// </summary>
public sealed class ZoneLicenseService(
    IZoneLicenseRepository licenses,
    IFarmingZoneRepository zones,
    IFileStorage storage,
    UploadedFileSaver fileSaver,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    // The same record type as the license notifications.
    private const string RecordType = RelatedRecordTypes.ZoneLicense;

    /// <summary>The licenses of the zone with their state as of <paramref name="asOf"/> (default today), newest first.</summary>
    public async Task<Result<ZoneLicenseOverviewView>> ListForZoneAsync(Guid zoneId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if ((await zones.GetByIdsAsync([zoneId], cancellationToken)).Count == 0)
        {
            return AppError.NotFound;
        }

        var day = asOf ?? timeProvider.Today();
        var all = await licenses.ListForZonesAsync([zoneId], cancellationToken);
        var newest = ZoneLicenseOverview.Newest(zoneId, all);
        return new ZoneLicenseOverviewView(zoneId, day, ZoneLicenseOverview.For(zoneId, all, day).Select(r => new ZoneLicenseRowView(
            r.License.Document.DocumentType, LicenseTypes.NameOf(r.License.Document.DocumentType), r.State, r.License.Document.Id,
            r.License.Document.DocumentNumber, r.License.Document.EffectiveDate, r.License.Document.ExpiryDate, r.License.IsReplaced,
            r.License == newest)).ToList());
    }

    public async Task<Result<ZoneLicenseView>> GetAsync(Guid documentId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var document = await licenses.GetForEditAsync(documentId, cancellationToken);
        if (document is null)
        {
            return AppError.NotFound;
        }

        var replacement = await licenses.FindReplacementAsync(document.Id, cancellationToken);
        return await ViewAsync(document, document.Zone, replacement, asOf ?? timeProvider.Today(), cancellationToken);
    }

    public async Task<Result<ZoneLicenseView>> CreateAsync(SaveZoneLicenseRequest request, IncomingFile? file, CancellationToken cancellationToken)
    {
        var (input, invalid) = ZoneLicenseInput.Read(request);
        var zone = input.ZoneId == Guid.Empty ? null : await zones.GetForEditAsync(input.ZoneId, cancellationToken);
        if (zone is not null && NotInUse(zone) is { } discontinued)
        {
            return discontinued;
        }

        var fields = invalid.ToList();
        if (zone is null && !fields.Contains("Zone"))
        {
            fields.Add("Zone");
        }

        var checkedFile = CheckFile(fields, file, fileRequired: true);
        if (fields.Count > 0)
        {
            return AppError.InvalidFields(fields);
        }

        var uploaded = await UploadAsync(checkedFile!, cancellationToken);
        if (uploaded is null)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var document = new ZoneComplianceDocument { ZoneId = zone!.Id };
        Apply(document, input, uploaded);
        licenses.Add(document);
        audit.Record(new AuditEntry("ZoneLicense.Created", RecordType)
        {
            RecordId = document.Id, RecordCode = document.DocumentNumber, ZoneId = document.ZoneId, After = Snapshot(document),
        });
        await fileSaver.SaveAsync(uploaded, null, cancellationToken);
        return await ViewAsync(document, zone, null, timeProvider.Today(), cancellationToken);
    }

    /// <summary>
    /// Corrects a license that is not Replaced, while its zone is In Use (FARM-03.3); the zone cannot change. A changed
    /// expiry date re-arms both reminders.
    /// </summary>
    public async Task<Result<ZoneLicenseView>> UpdateAsync(Guid documentId, SaveZoneLicenseRequest request, IncomingFile? file, CancellationToken cancellationToken)
    {
        var document = await licenses.GetForEditAsync(documentId, cancellationToken);
        if (document is null)
        {
            return AppError.NotFound;
        }

        if (await licenses.FindReplacementAsync(document.Id, cancellationToken) is { } replacement)
        {
            return AppError.Conflict(
                Replaced(document, replacement) with { LegacyCode = MessageCodes.Legacy.LicenseReplaced },
                new Dictionary<string, object?> { ["replacementId"] = replacement.Id });
        }

        if (NotInUse(document.Zone) is { } discontinued)
        {
            return discontinued;
        }

        var (input, invalid) = ZoneLicenseInput.Read(request, document.ZoneId);
        var fields = invalid.ToList();
        var checkedFile = CheckFile(fields, file, fileRequired: false);
        if (fields.Count > 0)
        {
            return AppError.InvalidFields(fields);
        }

        if (await rowVersions.CheckAsync(document, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        string? uploaded = null;
        if (checkedFile is not null && (uploaded = await UploadAsync(checkedFile, cancellationToken)) is null)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var before = Snapshot(document);
        var replacedFile = uploaded is null ? null : document.EvidenceFileUrl;
        ExpiryReminderRule.RearmOnNewExpiry(document, input.ExpiryDate);
        Apply(document, input, uploaded ?? document.EvidenceFileUrl);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(document));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("ZoneLicense.Updated", RecordType)
            {
                RecordId = document.Id, RecordCode = document.DocumentNumber, ZoneId = document.ZoneId, Before = changedBefore, After = changedAfter,
            });
        }

        await fileSaver.SaveAsync(uploaded, replacedFile, cancellationToken);
        return await ViewAsync(document, document.Zone, null, timeProvider.Today(), cancellationToken);
    }

    /// <summary>
    /// FARM-03.2: a new license of the same zone replacing this one, which becomes Replaced from today and stays unchanged
    /// with its own file (BR-FARM-06). Only a license that is not Replaced, of an In Use zone, can be replaced.
    /// </summary>
    public async Task<Result<ZoneLicenseView>> ReplaceAsync(Guid documentId, SaveZoneLicenseRequest request, IncomingFile? file, CancellationToken cancellationToken)
    {
        var old = await licenses.GetForEditAsync(documentId, cancellationToken);
        if (old is null)
        {
            return AppError.NotFound;
        }

        if (await licenses.FindReplacementAsync(old.Id, cancellationToken) is { } existing)
        {
            return new AppError(ErrorKind.Conflict, Replaced(old, existing) with { LegacyCode = MessageCodes.Legacy.Msg03 }, new Dictionary<string, object?>
            {
                ["existingId"] = existing.Id, ["replacementId"] = existing.Id,
            });
        }

        if (NotInUse(old.Zone) is { } discontinued)
        {
            return discontinued;
        }

        var (input, invalid) = ZoneLicenseInput.Read(request, old.ZoneId, old.DocumentType);
        var fields = invalid.ToList();
        var checkedFile = CheckFile(fields, file, fileRequired: true);
        if (fields.Count > 0)
        {
            return AppError.InvalidFields(fields);
        }

        var uploaded = await UploadAsync(checkedFile!, cancellationToken);
        if (uploaded is null)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var document = new ZoneComplianceDocument { ZoneId = old.ZoneId, ReplacesDocumentId = old.Id };
        Apply(document, input, uploaded);
        licenses.Add(document);
        audit.Record(new AuditEntry("ZoneLicense.Replaced", RecordType)
        {
            RecordId = document.Id,
            RecordCode = document.DocumentNumber,
            ZoneId = document.ZoneId,
            Before = new { ReplacedDocumentId = old.Id, ReplacedDocumentNumber = old.DocumentNumber },
            After = Snapshot(document),
        });
        await fileSaver.SaveAsync(uploaded, null, cancellationToken);
        return await ViewAsync(document, old.Zone, null, timeProvider.Today(), cancellationToken);
    }

    /// <summary>
    /// MSG-GEN-09 when the zone is not In Use: its licenses cannot be added or changed (GBR-01, FARM-03.0.E2). An In Use
    /// zone (tracked) becomes part of the save, as for rafts: a parallel discontinue then fails with MSG06.
    /// </summary>
    private AppError? NotInUse(FarmingZone zone)
    {
        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Farming zone {zone.Code}", statusCatalog.DisplayNameOf(zone.StatusId)));
        }

        unitOfWork.MarkChanged(zone);
        return null;
    }

    /// <summary>Adds "File" to <paramref name="fields"/> unless the file is a PDF, JPEG or PNG of at most 10 MB.</summary>
    private static ValidatedFile? CheckFile(List<string> fields, IncomingFile? file, bool fileRequired)
    {
        if (file is null && !fileRequired)
        {
            return null;
        }

        var result = file is null ? null : FileValidator.Validate([file], FileRules.Documents);
        if (result is not { IsValid: true })
        {
            fields.Add("File");
            return null;
        }

        return result.Files[0];
    }

    private async Task<string?> UploadAsync(ValidatedFile file, CancellationToken cancellationToken)
    {
        var result = await storage.UploadAsync(file, FileCategories.ZoneLicenses, cancellationToken);
        return result.Succeeded ? result.Key : null;
    }

    /// <summary>MSG-GEN-09 naming the replacement: a Replaced license is kept as it is (BR-FARM-06).</summary>
    private static UserMessage Replaced(ZoneComplianceDocument document, ZoneComplianceDocument replacement) =>
        UserMessages.WrongStatus($"License {document.DocumentNumber}", $"Replaced by {replacement.DocumentNumber}");

    private static void Apply(ZoneComplianceDocument document, ZoneLicenseInput input, string? fileKey)
    {
        document.DocumentType = input.DocumentType;
        document.DocumentNumber = input.DocumentNumber;
        document.EffectiveDate = input.EffectiveDate;
        document.ExpiryDate = input.ExpiryDate;
        document.IssuingAuthority = input.IssuingAuthority;
        document.Holder = input.Holder;
        document.LegalScope = input.LegalScope;
        document.EvidenceFileUrl = fileKey;
    }

    private async Task<ZoneLicenseView> ViewAsync(
        ZoneComplianceDocument d, FarmingZone zone, ZoneComplianceDocument? replacement, DateOnly asOf, CancellationToken cancellationToken)
    {
        var replacedOn = replacement is null ? (DateOnly?)null : BusinessClock.ToBusinessDate(replacement.CreatedAt);
        var state = LicenseStateRules.StateOn(new LicenseFacts(d.EffectiveDate, d.ExpiryDate, replacedOn), asOf);
        var fileUrl = d.EvidenceFileUrl is null ? null : await storage.GetReadUrlAsync(d.EvidenceFileUrl, cancellationToken: cancellationToken);
        var zoneRef = new ZoneRef(zone.Id, zone.Code, zone.Name);
        return new ZoneLicenseView(
            d.Id, d.DocumentType, LicenseTypes.NameOf(d.DocumentType), d.DocumentNumber, d.EffectiveDate, d.ExpiryDate,
            d.IssuingAuthority, d.Holder, d.LegalScope, asOf, state, zoneRef, [zoneRef], d.ReplacesDocumentId, replacement?.Id, fileUrl,
            RowVersionGuard.Encode(d.RowVersion));
    }

    private static object Snapshot(ZoneComplianceDocument d) => new
    {
        d.DocumentType, d.DocumentNumber, d.EffectiveDate, d.ExpiryDate, d.IssuingAuthority, d.Holder, d.LegalScope, d.ZoneId,
        File = d.EvidenceFileUrl,
    };
}
