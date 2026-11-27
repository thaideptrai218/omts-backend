using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Traceability.Origin;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Cooperatives.Landing;

/// <summary>
/// The public introduction page of the cooperative (owner decision of 27/11/2026): the Owner chooses its pictures and one
/// real invoice as the example; the products and certifications come from the catalogue and the profile.
/// </summary>
public sealed class LandingService(
    ILandingRepository landing,
    ICooperativeRepository cooperatives,
    CooperativeProfileViews cooperativeViews,
    IFileStorage storage,
    UploadedFileSaver fileSaver,
    AuditLogService audit,
    TimeProvider timeProvider)
{
    public const int MaxCaptionLength = 150;
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);
    private static readonly IReadOnlyDictionary<LandingSection, int> Limits = new Dictionary<LandingSection, int>
    {
        [LandingSection.Background] = 1,
        [LandingSection.Press] = 8,
        [LandingSection.Gallery] = 12,
    };

    public async Task<Result<LandingSettingsView>> SettingsAsync(CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        return await ViewAsync(cooperative, cancellationToken);
    }

    /// <summary>Adds a picture (PNG/JPEG/WebP, 5 MB); a new background replaces the current one.</summary>
    public async Task<Result<LandingSettingsView>> AddPhotoAsync(LandingSection section, string? caption, IncomingFile file, CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        if (!Limits.TryGetValue(section, out var limit))
        {
            return AppError.Invalid("Section");
        }

        var text = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (text?.Length > MaxCaptionLength)
        {
            return AppError.Invalid("Caption");
        }

        var photos = await landing.PhotosAsync(cancellationToken);
        var inSection = photos.Where(p => p.Section == section).ToList();
        if (section != LandingSection.Background && inSection.Count >= limit)
        {
            return AppError.Invalid("File", $"This section already has {limit} pictures.");
        }

        var checkedFile = FileValidator.Validate([file], FileRules.CoverPhoto);
        if (!checkedFile.IsValid)
        {
            return AppError.Invalid("File");
        }

        var uploaded = await storage.UploadAsync(checkedFile.Files[0], FileCategories.LandingPhotos, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var replaced = section == LandingSection.Background ? inSection.FirstOrDefault() : null;
        if (replaced is not null)
        {
            landing.Remove(replaced);
        }

        var photo = new LandingPhoto
        {
            Section = section,
            Caption = text,
            StorageKey = uploaded.Key!,
            SortOrder = inSection.Count == 0 ? 0 : inSection.Max(p => p.SortOrder) + 1,
        };
        landing.Add(photo);
        audit.Record(new AuditEntry("Landing.PhotoAdded", nameof(LandingPhoto)) { RecordId = photo.Id, After = new { photo.Section, photo.Caption, photo.StorageKey } });
        await fileSaver.SaveAsync(photo.StorageKey, replaced?.StorageKey, cancellationToken);
        return await ViewAsync(cooperative, cancellationToken);
    }

    public async Task<Result<LandingSettingsView>> RemovePhotoAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        if ((await landing.PhotosAsync(cancellationToken)).FirstOrDefault(p => p.Id == id) is not { } photo)
        {
            return AppError.NotFound;
        }

        landing.Remove(photo);
        audit.Record(new AuditEntry("Landing.PhotoRemoved", nameof(LandingPhoto)) { RecordId = photo.Id, Before = new { photo.Section, photo.Caption, photo.StorageKey } });
        await fileSaver.SaveAsync(null, photo.StorageKey, cancellationToken);
        return await ViewAsync(cooperative, cancellationToken);
    }

    /// <summary>Chooses the example invoice by its code; empty clears it. Its provenance page becomes reachable from the introduction page.</summary>
    public async Task<Result<LandingSettingsView>> SetFeaturedAsync(string? invoiceCode, CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        var code = string.IsNullOrWhiteSpace(invoiceCode) ? null : invoiceCode.Trim().ToUpperInvariant();
        if (code is not null && await landing.QrTokenOfAsync(code, cancellationToken) is null)
        {
            return AppError.Invalid("InvoiceCode", "No invoice with a QR code has this code.");
        }

        if (cooperative.FeaturedInvoiceCode != code)
        {
            audit.Record(new AuditEntry("Landing.ExampleInvoiceChanged", nameof(Cooperative))
            {
                RecordId = cooperative.Id,
                Before = new { cooperative.FeaturedInvoiceCode },
                After = new { FeaturedInvoiceCode = code },
            });
            cooperative.FeaturedInvoiceCode = code;
            await fileSaver.SaveAsync((string?)null, null, cancellationToken);
        }

        return await ViewAsync(cooperative, cancellationToken);
    }

    public async Task<Result<LandingPublicView>> PublicAsync(CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        var block = ProvenancePageBuilder.Cooperative(await cooperativeViews.PublicBlockAsync(cooperative, cancellationToken), ProvenanceLanguages.Vietnamese);
        var photos = new List<LandingPublicPhoto>();
        foreach (var photo in await landing.PhotosAsync(cancellationToken))
        {
            photos.Add(new LandingPublicPhoto(photo.Section, photo.Caption, await storage.GetReadUrlAsync(photo.StorageKey, LinkLifetime, cancellationToken)));
        }

        var today = BusinessClock.ToBusinessDate(timeProvider.GetUtcNow());
        var products = await ShowcaseProducts.ViewsAsync(await landing.ProductsOnSaleAsync(today, cancellationToken), storage, cancellationToken);
        var token = cooperative.FeaturedInvoiceCode is { } code ? await landing.QrTokenOfAsync(code, cancellationToken) : null;
        return new LandingPublicView(block, photos, products, token);
    }

    private async Task<LandingSettingsView> ViewAsync(Cooperative cooperative, CancellationToken cancellationToken)
    {
        var views = new List<LandingPhotoView>();
        foreach (var photo in await landing.PhotosAsync(cancellationToken))
        {
            views.Add(new LandingPhotoView(photo.Id, photo.Section, photo.Caption, await storage.GetReadUrlAsync(photo.StorageKey, LinkLifetime, cancellationToken)));
        }

        return new LandingSettingsView(views, cooperative.FeaturedInvoiceCode);
    }
}
