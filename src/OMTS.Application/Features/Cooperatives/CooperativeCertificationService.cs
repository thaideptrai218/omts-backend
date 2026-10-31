using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Cooperatives;

/// <summary>
/// FARM-01.1 – certifications of the cooperative (Cooperative Owner only, checked by the API filter): list, add, edit
/// and remove, each audited (GBR-02). The file is mandatory and uploaded first, so a storage failure saves nothing
/// (MSG-GEN-03, GFR-01); an upload whose save fails is removed again, and a replaced or removed file is deleted once
/// the change is saved (<see cref="UploadedFileSaver"/>). The expiry reminders of GFR-03 belong to the daily expiry
/// job, not to these saves.
/// </summary>
public sealed class CooperativeCertificationService(
    ICooperativeRepository cooperatives,
    IFileStorage storage,
    UploadedFileSaver fileSaver,
    CooperativeProfileViews views,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    TimeProvider timeProvider)
{
    public const int MaxName = 200;
    public const int MaxNumber = 100;
    public const int MaxIssuer = 200;

    private const string RecordType = nameof(CooperativeCertification);

    /// <summary>Every certification with its state as of today, by name.</summary>
    public async Task<Result<IReadOnlyList<CooperativeCertificationView>>> ListAsync(CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        var today = timeProvider.Today();
        var list = new List<CooperativeCertificationView>();
        foreach (var certification in await cooperatives.ListCertificationsAsync(cooperative.Id, cancellationToken))
        {
            list.Add(await views.CertificationAsync(certification, today, cancellationToken));
        }

        return list;
    }

    public async Task<Result<CooperativeCertificationSaved>> CreateAsync(
        SaveCooperativeCertificationRequest request, IncomingFile? file, CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        var (input, validated, invalid) = Read(request, file, fileRequired: true);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var uploaded = await UploadAsync(validated!, cancellationToken);
        if (uploaded is null)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var certification = new CooperativeCertification { CooperativeId = cooperative.Id };
        Apply(certification, input, uploaded);
        cooperatives.AddCertification(certification);
        audit.Record(new AuditEntry("CooperativeCertification.Created", RecordType)
        {
            RecordId = certification.Id, RecordCode = certification.Number, After = Snapshot(certification),
        });
        await fileSaver.SaveAsync(uploaded, null, cancellationToken);
        return await SavedAsync(certification, cancellationToken);
    }

    /// <summary>Corrects a certification; <c>file</c> is optional here (a new file replaces the stored one).</summary>
    public async Task<Result<CooperativeCertificationSaved>> UpdateAsync(
        Guid id, SaveCooperativeCertificationRequest request, IncomingFile? file, CancellationToken cancellationToken)
    {
        if (await FindAsync(id, cancellationToken) is not { } certification)
        {
            return AppError.NotFound;
        }

        var (input, validated, invalid) = Read(request, file, fileRequired: false);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await rowVersions.CheckAsync(certification, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        string? uploaded = null;
        if (validated is not null && (uploaded = await UploadAsync(validated, cancellationToken)) is null)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var before = Snapshot(certification);
        var replacedFile = uploaded is null ? null : certification.FileUrl;
        Apply(certification, input, uploaded ?? certification.FileUrl);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(certification));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("CooperativeCertification.Updated", RecordType)
            {
                RecordId = certification.Id, RecordCode = certification.Number, Before = changedBefore, After = changedAfter,
            });
        }

        await fileSaver.SaveAsync(uploaded, replacedFile, cancellationToken);
        return await SavedAsync(certification, cancellationToken);
    }

    /// <summary>
    /// Removes the certification and, once that is saved, its file; the audit log keeps its values. MSG-GEN-05
    /// "Certifications saved." (the list without it).
    /// </summary>
    public async Task<Result<CooperativeCertificationRemoved>> RemoveAsync(Guid id, string? rowVersion, CancellationToken cancellationToken)
    {
        if (await FindAsync(id, cancellationToken) is not { } certification)
        {
            return AppError.NotFound;
        }

        if (await rowVersions.CheckAsync(certification, rowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        audit.Record(new AuditEntry("CooperativeCertification.Removed", RecordType)
        {
            RecordId = certification.Id, RecordCode = certification.Number, Before = Snapshot(certification),
        });
        cooperatives.RemoveCertification(certification);
        await fileSaver.SaveAsync(null, certification.FileUrl, cancellationToken);
        var saved = UserMessages.Saved("Certifications");
        return new CooperativeCertificationRemoved(saved.Text, saved.Code);
    }

    private async Task<CooperativeCertification?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative
            ? await cooperatives.GetCertificationAsync(cooperative.Id, id, cancellationToken)
            : null;

    /// <summary>Name required; number, issuer and expiry date optional; the file (image or PDF, 10 MB) required on add.</summary>
    private static (Input Input, ValidatedFile? File, IReadOnlyList<string> Invalid) Read(
        SaveCooperativeCertificationRequest request, IncomingFile? file, bool fileRequired)
    {
        var check = new FieldCheck();
        var input = new Input(
            check.Required(request.Name, "Name", MaxName),
            check.Optional(request.Number, "Number", MaxNumber),
            check.Optional(request.Issuer, "Issuer", MaxIssuer),
            request.ExpiryDate);

        ValidatedFile? validated = null;
        if (file is not null || fileRequired)
        {
            var result = file is null ? null : FileValidator.Validate([file], FileRules.Documents);
            check.Check(result is { IsValid: true }, "File");
            validated = result is { IsValid: true } ? result.Files[0] : null;
        }

        return (input, validated, check.Invalid);
    }

    private async Task<string?> UploadAsync(ValidatedFile file, CancellationToken cancellationToken)
    {
        var result = await storage.UploadAsync(file, FileCategories.CooperativeCertifications, cancellationToken);
        return result.Succeeded ? result.Key : null;
    }

    private async Task<CooperativeCertificationSaved> SavedAsync(CooperativeCertification certification, CancellationToken cancellationToken)
    {
        var saved = UserMessages.Saved("Certification");
        return new CooperativeCertificationSaved(
            await views.CertificationAsync(certification, timeProvider.Today(), cancellationToken), saved.Text, saved.Code);
    }

    private static void Apply(CooperativeCertification c, Input input, string? fileKey)
    {
        c.Name = input.Name;
        c.Number = input.Number;
        c.Issuer = input.Issuer;
        c.ExpiryDate = input.ExpiryDate;
        c.FileUrl = fileKey;
    }

    private static object Snapshot(CooperativeCertification c) => new { c.Name, c.Number, c.Issuer, c.ExpiryDate, File = c.FileUrl };

    private sealed record Input(string Name, string? Number, string? Issuer, DateOnly? ExpiryDate);
}
