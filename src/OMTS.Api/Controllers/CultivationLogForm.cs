namespace OMTS.Api.Controllers;

/// <summary>Multipart fields of a cultivation log. <see cref="KeepPhotoKeys"/> and <see cref="Reason"/> belong to a correction.</summary>
public class CultivationLogForm
{
    /// <summary><c>yyyy-MM-dd</c>.</summary>
    public string? VisitDate { get; set; }
    public string? Notes { get; set; }

    /// <summary>One field per photo to keep; a photo left out is removed.</summary>
    public string?[]? KeepPhotoKeys { get; set; }
    public string? Reason { get; set; }

    /// <summary>One part per new photo: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }
}

/// <summary>Multipart fields of TRACE-03.1: those of a log correction and the Zone Manager's answer to MSG-TRACE-04.</summary>
public sealed class TraceCultivationLogForm : CultivationLogForm
{
    /// <summary><c>true</c> when the Cooperative Owner agreed in person that the data is wrong; the Owner never sends it.</summary>
    public bool OwnerApproved { get; set; }
}
