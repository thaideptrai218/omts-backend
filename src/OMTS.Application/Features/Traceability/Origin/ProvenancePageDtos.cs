using OMTS.Application.Features.Cooperatives;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Origin;

/// <summary>
/// The language of the provenance page (BR-ORIGIN-02): Vietnamese, the language of the system, unless English is asked for. The fixed texts are the client's; the messages and the
/// cooperative introduction follow this language.
/// </summary>
public static class ProvenanceLanguages
{
    public const string Vietnamese = "vi";
    public const string English = "en";

    /// <summary>"en" (any case) is English; anything else, also no value, opens the page in Vietnamese.</summary>
    public static string Of(string? lang) => string.Equals(lang?.Trim(), English, StringComparison.OrdinalIgnoreCase) ? English : Vietnamese;
}

/// <summary>Ok: lots are listed. NoLots: no lot of the invoice is Published or Withdrawn (MSG-ORIGIN-02). Cancelled: the cooperative block only (MSG-ORIGIN-04).</summary>
public enum ProvenancePageStatus
{
    Ok,
    NoLots,
    Cancelled,
}

/// <summary>Where the map pin comes from: the raft, or the zone when the raft has no map location (BR-ORIGIN-03).</summary>
public enum ProvenanceMapSource
{
    Raft,
    Zone,
}

/// <summary>
/// SCR-ORIGIN-01, the public provenance page of one sales invoice. A whitelist (BR-ORIGIN-01, BR-ORIGIN-04, NFR-03): no customer, price, amount, payment, debt, quantity, return,
/// claim, supplier, withdrawal reason or internal id; a test fixes the exact set of JSON properties.
/// </summary>
/// <param name="Language">"vi" or "en": the language of <paramref name="Message"/>, of the lot messages and of the cooperative introduction.</param>
/// <param name="Message">MSG-ORIGIN-02 or MSG-ORIGIN-04 in the page language; null when lots are listed.</param>
/// <param name="MessageParams">The atomic values of the message (e.g. <c>cooperative_phone</c>), for a client that builds the text itself.</param>
/// <param name="Invoice">The code and date of the invoice; null on a cancelled invoice, whose page shows the cooperative block only (ORIGIN-01.0.E2).</param>
/// <param name="Lots">The Published and Withdrawn lots of the invoice, in the order of the invoice lines.</param>
public sealed record ProvenancePage(
    string Language,
    ProvenancePageStatus Status,
    string? Message,
    string? MessageCode,
    IReadOnlyDictionary<string, object?>? MessageParams,
    ProvenancePageInvoice? Invoice,
    ProvenanceCooperative Cooperative,
    IReadOnlyList<ProvenanceLot> Lots);

public sealed record ProvenancePageInvoice(string Code, DateOnly Date);

/// <summary>The cooperative block: the current profile as BR-FARM-09 shows it, the introduction in the page language (Vietnamese when the English one is empty).</summary>
public sealed record ProvenanceCooperative(
    string FullName,
    string? ShortName,
    int? YearEstablished,
    string? OfficeAddress,
    string? PhoneNumber,
    string? Email,
    string? Website,
    string? SocialLink,
    string? LogoUrl,
    string? CoverPhotoUrl,
    string? Introduction,
    IReadOnlyList<PublicCertification> Certifications);

/// <summary>A listed lot (BR-ORIGIN-05): a Published lot with its origin information, or a Withdrawn lot with the withdrawal date and MSG-ORIGIN-01, never the reason.</summary>
/// <param name="Products">The names of the products sold from the lot on this invoice, in line order (no kg).</param>
/// <param name="WithdrawalDate">The business date of the withdrawal; null on a Published lot.</param>
/// <param name="Message">MSG-ORIGIN-01 in the page language on a Withdrawn lot; null on a Published lot.</param>
/// <param name="Origin">Null on a Withdrawn lot.</param>
public sealed record ProvenanceLot(
    string LotCode,
    IReadOnlyList<string> Products,
    HarvestLotOriginState OriginState,
    DateOnly? WithdrawalDate,
    string? Message,
    string? MessageCode,
    IReadOnlyDictionary<string, object?>? MessageParams,
    ProvenanceOrigin? Origin);

/// <summary>The origin information of a Published lot (BR-ORIGIN-03), always the current data (BR-TRACE-05).</summary>
/// <param name="Map">The raft's map location, or the zone's; null when neither has one.</param>
/// <param name="Seedings">Each seeding date of the lot's lanes with the seed item hung, oldest first (no supplier, no quantity).</param>
/// <param name="DaysOfFarming">From the earliest seeding date to the harvest date; null when no session is found.</param>
/// <param name="HarvestPhotoUrls">Signed read URLs (10 minutes): ask the page again rather than keeping them.</param>
/// <param name="RaftPhotoUrls">Signed read URLs (10 minutes).</param>
public sealed record ProvenanceOrigin(
    DateOnly HarvestDate,
    string RaftCode,
    string ZoneName,
    ProvenanceMapPoint? Map,
    IReadOnlyList<ProvenanceSeeding> Seedings,
    int? DaysOfFarming,
    IReadOnlyList<string> HarvestPhotoUrls,
    IReadOnlyList<string> RaftPhotoUrls);

public sealed record ProvenanceMapPoint(decimal Latitude, decimal Longitude, ProvenanceMapSource Source);

public sealed record ProvenanceSeeding(DateOnly SeedingDate, string SeedItemName);
