using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>
/// FARM-03 – zone licenses (Cooperative Owner only, checked by the API filter): create, view, edit and
/// replace; never deleted. One document may cover several zones. The file is uploaded first, so a storage
/// failure saves nothing; an upload whose database save fails is removed again, and a file replaced by an edit is
/// removed once the edit is saved (<see cref="UploadedFileSaver"/>).
/// </summary>
public sealed class ZoneLicenseService(
    IZoneLicenseRepository licenses,
    IFarmingZoneRepository zones,
    IFileStorage storage,
    UploadedFileSaver fileSaver,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = "ZoneLicense";

    public async Task<Result<ZoneLicenseOverviewView>> ListForZoneAsync(Guid zoneId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if ((await zones.GetByIdsAsync([zoneId], cancellationToken)).Count == 0)
        {
            return AppError.NotFound;
        }

        var day = asOf ?? timeProvider.Today();
        var rows = ZoneLicenseOverview.For(zoneId, await licenses.ListCoveringAsync([zoneId], cancellationToken), day);
        return new ZoneLicenseOverviewView(zoneId, day, rows.Select(r => new ZoneLicenseRowView(
            r.DocumentType, LicenseTypes.Names[r.DocumentType], r.State, r.Document?.Id, r.Document?.DocumentNumber,
            r.Document?.EffectiveDate, r.Document?.ExpiryDate, r.IsReplaced)).ToList());
    }

    public async Task<Result<ZoneLicenseView>> GetAsync(Guid documentId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var document = await licenses.GetForEditAsync(documentId, cancellationToken);
        if (document is null)
        {
            return AppError.NotFound;
        }

        var replacement = await licenses.FindReplacementAsync(document.Id, cancellationToken);
        var zoneRefs = document.ZoneScopes.Select(s => new ZoneRef(s.ZoneId, s.Zone.Code, s.Zone.Name)).ToList();
        return await ViewAsync(document, zoneRefs, replacement, asOf ?? timeProvider.Today(), cancellationToken);
    }

    public async Task<Result<ZoneLicenseView>> CreateAsync(SaveZoneLicenseRequest request, IncomingFile? file, CancellationToken cancellationToken)
    {
        var (input, invalid) = ZoneLicenseInput.Read(request);
        var checkedInput = await CheckAsync(input, invalid, file, fileRequired: true, cancellationToken);
        if (checkedInput.Error is { } error)
        {
            return error;
        }

        var document = new ZoneComplianceDocument();
        var uploaded = await UploadAsync(checkedInput.File!, cancellationToken);
        if (uploaded is null)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        Apply(document, input, uploaded);
        licenses.Add(document);
        SyncScopes(document, input);
        audit.Record(new AuditEntry("ZoneLicense.Created", RecordType)
        {
            RecordId = document.Id, RecordCode = document.DocumentNumber, ZoneId = input.ZoneIds[0], After = Snapshot(document, input.ZoneIds),
        });
        await fileSaver.SaveAsync(uploaded, null, cancellationToken);
        return await ViewAsync(document, checkedInput.Zones, null, timeProvider.Today(), cancellationToken);
    }

    /// <summary>Corrects a license that has not been replaced; a changed expiry date re-arms both reminders.</summary>
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

        var (input, invalid) = ZoneLicenseInput.Read(request);
        var checkedInput = await CheckAsync(input, invalid, file, fileRequired: false, cancellationToken);
        if (checkedInput.Error is { } error)
        {
            return error;
        }

        if (await rowVersions.CheckAsync(document, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        string? uploaded = null;
        if (checkedInput.File is { } newFile && (uploaded = await UploadAsync(newFile, cancellationToken)) is null)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var before = Snapshot(document, document.ZoneScopes.Select(s => s.ZoneId).ToList());
        var replacedFile = uploaded is null ? null : document.EvidenceFileUrl;
        if (document.ExpiryDate != input.ExpiryDate)
        {
            document.ExpiringReminderSentAt = null;
            document.ExpiredReminderSentAt = null;
        }

        Apply(document, input, uploaded ?? document.EvidenceFileUrl);
        if (SyncScopes(document, input))
        {
            // Scope rows only: the license itself must still get a new row version.
            unitOfWork.MarkChanged(document);
        }
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(document, input.ZoneIds));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("ZoneLicense.Updated", RecordType)
            {
                RecordId = document.Id, RecordCode = document.DocumentNumber, ZoneId = input.ZoneIds[0], Before = changedBefore, After = changedAfter,
            });
        }

        await fileSaver.SaveAsync(uploaded, replacedFile, cancellationToken);
        return await ViewAsync(document, checkedInput.Zones, null, timeProvider.Today(), cancellationToken);
    }

    /// <summary>
    /// Renewal or replacement: a new license pointing to the latest one of its chain, which stays unchanged with its
    /// own file.
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
            // FARM-03.2: only a license that is not Replaced can be replaced (MSG-GEN-09; MSG03 before).
            return new AppError(ErrorKind.Conflict, Replaced(old, existing) with { LegacyCode = MessageCodes.Legacy.Msg03 }, new Dictionary<string, object?>
            {
                ["existingId"] = existing.Id, ["replacementId"] = existing.Id,
            });
        }

        var defaults = new ZoneLicenseInput(
            old.DocumentType, old.DocumentNumber, old.EffectiveDate ?? default, old.ExpiryDate, null, null, null,
            old.ZoneScopes.Select(s => s.ZoneId).ToList());
        var (input, invalid) = ZoneLicenseInput.Read(request, defaults);
        var checkedInput = await CheckAsync(input, invalid, file, fileRequired: true, cancellationToken);
        if (checkedInput.Error is { } error)
        {
            return error;
        }

        var uploaded = await UploadAsync(checkedInput.File!, cancellationToken);
        if (uploaded is null)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var document = new ZoneComplianceDocument { ReplacesDocumentId = old.Id };
        Apply(document, input, uploaded);
        licenses.Add(document);
        SyncScopes(document, input);
        audit.Record(new AuditEntry("ZoneLicense.Replaced", RecordType)
        {
            RecordId = document.Id,
            RecordCode = document.DocumentNumber,
            ZoneId = input.ZoneIds[0],
            Before = new { ReplacedDocumentId = old.Id, ReplacedDocumentNumber = old.DocumentNumber },
            After = Snapshot(document, input.ZoneIds),
        });
        await fileSaver.SaveAsync(uploaded, null, cancellationToken);
        return await ViewAsync(document, checkedInput.Zones, null, timeProvider.Today(), cancellationToken);
    }

    private async Task<CheckedInput> CheckAsync(
        ZoneLicenseInput input, IReadOnlyList<string> invalid, IncomingFile? file, bool fileRequired, CancellationToken cancellationToken)
    {
        var fields = invalid.ToList();
        var found = input.ZoneIds.Count == 0 ? [] : await zones.GetByIdsAsync(input.ZoneIds, cancellationToken);
        if (input.ZoneIds.Count > 0 && found.Count != input.ZoneIds.Count && !fields.Contains("Zones"))
        {
            fields.Add("Zones");
        }

        ValidatedFile? validated = null;
        if (file is not null || fileRequired)
        {
            var result = file is null ? null : FileValidator.Validate([file], FileRules.Documents);
            if (result is not { IsValid: true })
            {
                fields.Add("File");
            }
            else
            {
                validated = result.Files[0];
            }
        }

        var zoneRefs = found.OrderBy(z => input.ZoneIds.ToList().IndexOf(z.Id)).Select(z => new ZoneRef(z.Id, z.Code, z.Name)).ToList();
        return new CheckedInput(fields.Count > 0 ? AppError.InvalidFields(fields) : null, validated, zoneRefs);
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

    /// <summary>One scope row per covered zone, valid over the license dates; true when zones were added or removed.</summary>
    private bool SyncScopes(ZoneComplianceDocument document, ZoneLicenseInput input)
    {
        var removedScopes = document.ZoneScopes.Where(s => !input.ZoneIds.Contains(s.ZoneId)).ToList();
        var addedZones = input.ZoneIds.Where(id => document.ZoneScopes.All(s => s.ZoneId != id)).ToList();
        foreach (var removed in removedScopes)
        {
            document.ZoneScopes.Remove(removed);
            licenses.RemoveScope(removed);
        }

        foreach (var scope in document.ZoneScopes)
        {
            (scope.ValidFrom, scope.ValidTo) = (input.EffectiveDate, input.ExpiryDate);
        }

        foreach (var zoneId in addedZones)
        {
            var scope = new DocumentZoneScope { DocumentId = document.Id, ZoneId = zoneId, ValidFrom = input.EffectiveDate, ValidTo = input.ExpiryDate };
            document.ZoneScopes.Add(scope);
            licenses.AddScope(scope);
        }

        return removedScopes.Count > 0 || addedZones.Count > 0;
    }

    private async Task<ZoneLicenseView> ViewAsync(
        ZoneComplianceDocument d, IReadOnlyList<ZoneRef> zoneRefs, ZoneComplianceDocument? replacement, DateOnly asOf, CancellationToken cancellationToken)
    {
        var state = LicenseStateRules.StateOn(new LicenseFacts(d.EffectiveDate, d.ExpiryDate, replacement is null ? [] : [replacement.EffectiveDate]), asOf);
        var fileUrl = d.EvidenceFileUrl is null ? null : await storage.GetReadUrlAsync(d.EvidenceFileUrl, cancellationToken: cancellationToken);
        return new ZoneLicenseView(
            d.Id, d.DocumentType, LicenseTypes.Names.GetValueOrDefault(d.DocumentType, d.DocumentType), d.DocumentNumber,
            d.EffectiveDate, d.ExpiryDate, d.IssuingAuthority, d.Holder, d.LegalScope, asOf, state, zoneRefs,
            d.ReplacesDocumentId, replacement?.Id, fileUrl, RowVersionGuard.Encode(d.RowVersion));
    }

    private static object Snapshot(ZoneComplianceDocument d, IReadOnlyList<Guid> zoneIds) => new
    {
        d.DocumentType, d.DocumentNumber, d.EffectiveDate, d.ExpiryDate, d.IssuingAuthority, d.Holder, d.LegalScope,
        ZoneIds = zoneIds.Order().ToList(), File = d.EvidenceFileUrl,
    };

    private sealed record CheckedInput(AppError? Error, ValidatedFile? File, IReadOnlyList<ZoneRef> Zones);
}
