using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Cooperatives;

/// <summary>
/// FARM-01 – the Cooperative Owner keeps the single cooperative record correct. There is no create or
/// delete; the receiving bank account belongs to PAY-01 and is neither returned nor changed here.
/// </summary>
public sealed partial class CooperativeProfileService(
    ICooperativeRepository cooperatives,
    IFileStorage storage,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    /// <summary>The current logo (whatever it is when the page is opened).</summary>
    public const string PublicLogoPath = "/api/public/cooperative/logo";

    /// <summary>
    /// One logo file, addressed by its storage path below <see cref="LogoCategory"/>: the URL never changes
    /// meaning, so a published provenance version that stored it keeps the logo of its publication time.
    /// Old logo files are never deleted.
    /// </summary>
    public const string PublicLogoFilesPath = "/api/public/cooperative/logos";

    private const string LogoCategory = "cooperative-logos";
    private const int MaxText = 255;
    private const int MaxAddress = 500;
    // Cooperatives.BusinessLines column length.
    private const int MaxBusinessLinesJson = 2000;

    // Stored text only (never written into HTML), so letters such as "ư" stay unescaped and fit the column.
    private static readonly JsonSerializerOptions LinesJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<Result<CooperativeProfileView>> GetAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative ? View(cooperative) : AppError.NotFound;

    public async Task<Result<CooperativeProfileSaved>> UpdateAsync(UpdateCooperativeProfileRequest request, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (cooperative is null)
        {
            return AppError.NotFound;
        }

        var (values, invalid) = Validate(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await cooperatives.FindByTaxCodeAsync(values.TaxCode, cooperative.Id, cancellationToken) is { } other)
        {
            return AppError.AlreadyExists("Tax code", other.Id);
        }

        if (await rowVersions.CheckAsync(cooperative, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(cooperative);
        cooperative.FullName = values.FullName;
        cooperative.TaxCode = values.TaxCode;
        cooperative.OfficeAddress = values.OfficeAddress;
        cooperative.PhoneNumber = values.PhoneNumber;
        cooperative.Email = values.Email;
        cooperative.Website = values.Website;
        cooperative.LegalRepresentative = values.LegalRepresentative;
        cooperative.BusinessLines = JsonSerializer.Serialize(values.BusinessLines, LinesJson);

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(cooperative));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Cooperative.Updated", nameof(Cooperative)) { RecordId = cooperative.Id, Before = changedBefore, After = changedAfter });
        }

        // The first complete save ends the Preliminary status; a Completed profile never goes back.
        if (cooperative.StatusId == StatusIds.Cooperative.Draft)
        {
            cooperative.StatusId = StatusIds.Cooperative.Completed;
            audit.RecordStatusChange(nameof(Cooperative), cooperative.Id, null, StatusIds.Cooperative.Draft, StatusIds.Cooperative.Completed);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CooperativeProfileSaved(View(cooperative), UserMessages.Saved("Cooperative profile").Text);
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

        var uploaded = await storage.UploadAsync(checkedFile.Files[0], LogoCategory, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.FileOperationFailed);
        }

        return await SetLogoAsync(cooperative, uploaded.Key, cancellationToken);
    }

    public async Task<Result<CooperativeProfileView>> RemoveLogoAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) switch
        {
            null => AppError.NotFound,
            // Nothing to remove: no change, no audit row.
            { LogoUrl: null } cooperative => View(cooperative),
            var cooperative => await SetLogoAsync(cooperative, null, cancellationToken),
        };

    /// <summary>The current logo file for the public page, or null when there is none.</summary>
    public async Task<(Stream Content, string ContentType)?> OpenLogoAsync(CancellationToken cancellationToken) =>
        await OpenAsync((await cooperatives.GetCurrentAsync(cancellationToken))?.LogoUrl, cancellationToken);

    /// <summary>
    /// One logo file by its path below the logo category ("2026/10/{id}.png"), current or replaced. Only
    /// logo files can be reached: the category is fixed here and the path must have the storage key shape.
    /// </summary>
    public async Task<(Stream Content, string ContentType)?> OpenLogoFileAsync(string path, CancellationToken cancellationToken) =>
        LogoFilePattern().IsMatch(path) ? await OpenAsync($"{LogoCategory}/{path}", cancellationToken) : null;

    private async Task<(Stream Content, string ContentType)?> OpenAsync(string? key, CancellationToken cancellationToken)
    {
        if (key is null || FileValidator.ContentTypeOfKey(key) is not { } contentType || await storage.OpenReadAsync(key, cancellationToken) is not { } content)
        {
            return null;
        }

        return (content, contentType);
    }

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
        return View(cooperative);
    }

    private static (Values Values, List<string> Invalid) Validate(UpdateCooperativeProfileRequest request)
    {
        var invalid = new List<string>();
        string Required(string? value, string field, int max)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 || trimmed.Length > max)
            {
                invalid.Add(field);
            }

            return trimmed;
        }

        var fullName = Required(request.FullName, "Full name", MaxText);
        var taxCode = Required(request.TaxCode, "Tax code", 14);
        if (taxCode.Length > 0 && !TaxCodePattern().IsMatch(taxCode) && !invalid.Contains("Tax code"))
        {
            invalid.Add("Tax code");
        }

        var address = Required(request.OfficeAddress, "Office address", MaxAddress);
        var phone = PhoneNumbers.Normalize(request.PhoneNumber);
        if (phone is null)
        {
            invalid.Add("Phone number");
        }

        string? email = null;
        if (!string.IsNullOrWhiteSpace(request.Email) && ((email = EmailAddresses.Normalize(request.Email)) is null || email.Length > MaxText))
        {
            invalid.Add("Email");
        }

        string? website = null;
        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            website = request.Website.Trim();
            if (website.Length > MaxText || !Uri.TryCreate(website, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                invalid.Add("Website");
            }
        }

        var representative = Required(request.LegalRepresentative, "Legal representative", MaxText);
        var lines = (request.BusinessLines ?? []).Select(l => l?.Trim() ?? string.Empty).ToList();
        if (lines.Count == 0 || lines.Any(l => l.Length == 0 || l.Length > MaxText) || JsonSerializer.Serialize(lines, LinesJson).Length > MaxBusinessLinesJson)
        {
            invalid.Add("Business lines");
        }

        return (new Values(fullName, taxCode, address, phone ?? string.Empty, email, website, representative, lines), invalid);
    }

    private static object Snapshot(Cooperative c) => new
    {
        c.FullName, c.TaxCode, c.OfficeAddress, c.PhoneNumber, c.Email, c.Website, c.LegalRepresentative, BusinessLines = Lines(c),
    };

    private static IReadOnlyList<string> Lines(Cooperative c) =>
        string.IsNullOrEmpty(c.BusinessLines) ? [] : JsonSerializer.Deserialize<List<string>>(c.BusinessLines) ?? [];

    private CooperativeProfileView View(Cooperative c)
    {
        // The URL of this very file: a replaced logo gets a new URL, and the old one keeps working.
        var logoUrl = c.LogoUrl is { } key && key.StartsWith($"{LogoCategory}/", StringComparison.Ordinal)
            ? $"{PublicLogoFilesPath}/{key[(LogoCategory.Length + 1)..]}"
            : null;
        return new CooperativeProfileView(
            c.Id, c.FullName, c.TaxCode, c.OfficeAddress, c.PhoneNumber, c.Email, c.Website, c.LegalRepresentative, Lines(c), logoUrl,
            statusCatalog.DisplayNameOf(c.StatusId),
            new PublicCooperativePreview(c.FullName, c.OfficeAddress, logoUrl),
            RowVersionGuard.Encode(c.RowVersion));
    }

    private sealed record Values(
        string FullName, string TaxCode, string OfficeAddress, string PhoneNumber, string? Email, string? Website,
        string LegalRepresentative, IReadOnlyList<string> BusinessLines);

    [GeneratedRegex(@"\A[0-9]{10}(-[0-9]{3})?\z")]
    private static partial Regex TaxCodePattern();

    [GeneratedRegex(@"\A[0-9]{4}/[0-9]{2}/[a-f0-9]{32}\.(jpg|png|webp)\z")]
    private static partial Regex LogoFilePattern();
}
