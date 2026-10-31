using System.Text.Json;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Cooperatives;

/// <summary>
/// FARM-01 – the Cooperative Owner keeps the single cooperative record correct. There is no create or
/// delete, and no status (SRS 27/10); the receiving bank account belongs to PAY-01 and is neither returned nor changed
/// here. The cover photo (<see cref="CooperativeCoverPhotoService"/>) and the certifications
/// (<see cref="CooperativeCertificationService"/>) have services of their own.
/// </summary>
public sealed class CooperativeProfileService(
    ICooperativeRepository cooperatives,
    IFileStorage storage,
    CooperativeProfileViews views,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<CooperativeProfileView>> GetAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative ? await views.ProfileAsync(cooperative, cancellationToken) : AppError.NotFound;

    public async Task<Result<CooperativeProfileSaved>> UpdateAsync(UpdateCooperativeProfileRequest request, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (cooperative is null)
        {
            return AppError.NotFound;
        }

        var (input, invalid) = CooperativeProfileInput.Read(request, timeProvider.Today().Year);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await cooperatives.FindByTaxCodeAsync(input.TaxCode, cooperative.Id, cancellationToken) is { } other)
        {
            return AppError.AlreadyExists("Tax code", other.Id);
        }

        if (await rowVersions.CheckAsync(cooperative, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(cooperative);
        Apply(cooperative, input);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(cooperative));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Cooperative.Updated", nameof(Cooperative)) { RecordId = cooperative.Id, Before = changedBefore, After = changedAfter });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved("Cooperative profile");
        return new CooperativeProfileSaved(await views.ProfileAsync(cooperative, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>Replaces the logo at once (PNG/JPEG/WebP, 2 MB). The old file is kept: published versions may use it.</summary>
    public async Task<Result<CooperativeProfileView>> UploadLogoAsync(IncomingFile file, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (cooperative is null)
        {
            return AppError.NotFound;
        }

        var checkedFile = FileValidator.Validate([file], FileRules.Logo);
        if (!checkedFile.IsValid)
        {
            return AppError.Invalid("Logo");
        }

        var uploaded = await storage.UploadAsync(checkedFile.Files[0], FileCategories.CooperativeLogos, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        return await SetLogoAsync(cooperative, uploaded.Key, cancellationToken);
    }

    public async Task<Result<CooperativeProfileView>> RemoveLogoAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) switch
        {
            null => AppError.NotFound,
            // Nothing to remove: no change, no audit row.
            { LogoUrl: null } cooperative => await views.ProfileAsync(cooperative, cancellationToken),
            var cooperative => await SetLogoAsync(cooperative, null, cancellationToken),
        };

    /// <summary>The current logo file for the public page, or null when there is none.</summary>
    public async Task<(Stream Content, string ContentType)?> OpenLogoAsync(CancellationToken cancellationToken) =>
        await CooperativePublicFiles.OpenAsync(storage, (await cooperatives.GetCurrentAsync(cancellationToken))?.LogoUrl, cancellationToken);

    /// <summary>
    /// One logo file by its path below the logo category ("2026/10/{id}.png"), current or replaced. Only
    /// logo files can be reached: the category is fixed here and the path must have the storage key shape.
    /// </summary>
    public async Task<(Stream Content, string ContentType)?> OpenLogoFileAsync(string path, CancellationToken cancellationToken) =>
        await CooperativePublicFiles.OpenAsync(storage, CooperativePublicFiles.KeyOf(path, FileCategories.CooperativeLogos), cancellationToken);

    private async Task<Result<CooperativeProfileView>> SetLogoAsync(Cooperative cooperative, string? key, CancellationToken cancellationToken)
    {
        audit.Record(new AuditEntry("Cooperative.LogoChanged", nameof(Cooperative))
        {
            RecordId = cooperative.Id,
            Before = new { cooperative.LogoUrl },
            After = new { LogoUrl = key },
        });
        cooperative.LogoUrl = key;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ProfileAsync(cooperative, cancellationToken);
    }

    private static void Apply(Cooperative c, CooperativeProfileInput input)
    {
        c.FullName = input.FullName;
        c.ShortName = input.ShortName;
        c.TaxCode = input.TaxCode;
        c.YearEstablished = input.YearEstablished;
        c.OfficeAddress = input.OfficeAddress;
        c.PhoneNumber = input.PhoneNumber;
        c.Email = input.Email;
        c.Website = input.Website;
        c.SocialLink = input.SocialLink;
        c.IntroductionVi = input.IntroductionVi;
        c.IntroductionEn = input.IntroductionEn;
        c.LegalRepresentative = input.LegalRepresentative;
        c.BusinessLines = input.BusinessLines.Count == 0 ? null : JsonSerializer.Serialize(input.BusinessLines, CooperativeProfileInput.LinesJson);
    }

    private static object Snapshot(Cooperative c) => new
    {
        c.FullName, c.ShortName, c.TaxCode, c.YearEstablished, c.OfficeAddress, c.PhoneNumber, c.Email, c.Website, c.SocialLink,
        c.IntroductionVi, c.IntroductionEn, c.LegalRepresentative, BusinessLines = CooperativeProfileViews.BusinessLinesOf(c),
    };
}
