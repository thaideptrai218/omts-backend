namespace OMTS.Api.Controllers;

/// <summary>Multipart fields of the manual entry with a photo; values are parsed with the invariant culture.</summary>
public sealed class RecordWaterMeasurementForm
{
    public string? ClientOperationId { get; set; }
    public string? MeasuredAt { get; set; }
    public string? Ph { get; set; }
    public string? Salinity { get; set; }
    public string? Confirm { get; set; }
    public IFormFile? Photo { get; set; }
}
