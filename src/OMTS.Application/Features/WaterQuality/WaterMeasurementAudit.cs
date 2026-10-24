using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>Audit actions of the water module and the values they record (NFR-02).</summary>
public static class WaterMeasurementAudit
{
    public const string RecordType = nameof(WaterMeasurement);
    public const string Created = "WaterMeasurement.Created";
    public const string Confirmed = "WaterMeasurement.Confirmed";
    public const string Corrected = "WaterMeasurement.Corrected";
    public const string Voided = "WaterMeasurement.Voided";
    public const string ThresholdUpdated = "WaterThreshold.Updated";

    /// <summary>Values and status of a measurement as written to the audit log.</summary>
    public static object Snapshot(WaterMeasurement m, IStatusCatalog statusCatalog) => new
    {
        Status = statusCatalog.CodeOf(m.StatusId),
        m.MeasuredAt,
        m.Ph,
        m.Salinity,
        m.InputSource,
        m.RecordVersion,
        m.ThresholdExceeded,
        m.ViolatedValues,
    };
}
