using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// E20 – pH and salinity of a zone. OCR values are only a draft until confirmed (BR-06, NFR-05).
/// A correction creates a new Confirmed record pointing to the corrected one, which becomes Corrected.
/// </summary>
public class WaterMeasurement : AuditableEntity, IHasStatus
{
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    public string? MeasurementPoint { get; set; }
    public DateTimeOffset MeasuredAt { get; set; }

    /// <summary>Confirmed values; may be empty while Draft.</summary>
    public decimal? Ph { get; set; }

    /// <summary>Salinity in ‰.</summary>
    public decimal? Salinity { get; set; }

    public MeasurementInputSource InputSource { get; set; }
    public string? PhotoUrl { get; set; }

    /// <summary>Initial OCR draft, kept for comparison with the confirmed values.</summary>
    public decimal? OcrPh { get; set; }
    public decimal? OcrSalinity { get; set; }
    public DateTimeOffset? OcrMeasuredAt { get; set; }

    public Guid? MeasuredByAccountId { get; set; }
    public Guid? ConfirmedByAccountId { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }

    public bool ThresholdExceeded { get; set; }
    public string? ViolatedValues { get; set; }

    /// <summary>Snapshot of the zone thresholds used when the alert was raised (BR-07).</summary>
    public decimal? PhMinUsed { get; set; }
    public decimal? PhMaxUsed { get; set; }
    public decimal? SalinityMinUsed { get; set; }
    public decimal? SalinityMaxUsed { get; set; }
    public DateTimeOffset? AlertedAt { get; set; }

    public int RecordVersion { get; set; } = 1;

    public Guid? CorrectedFromMeasurementId { get; set; }
    public WaterMeasurement? CorrectedFromMeasurement { get; set; }
    public string? CorrectionReason { get; set; }

    public int StatusId { get; set; } = StatusIds.WaterMeasurement.Draft;
    public Status Status { get; set; } = null!;
}
