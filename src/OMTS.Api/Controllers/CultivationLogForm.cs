namespace OMTS.Api.Controllers;

/// <summary>Multipart fields of a cultivation log. <see cref="KeepPhotoKeys"/>, <see cref="Reason"/> and <see cref="RowVersion"/> belong to a correction.</summary>
public sealed class CultivationLogForm
{
    /// <summary><c>yyyy-MM-dd</c>.</summary>
    public string? VisitDate { get; set; }
    public string? Notes { get; set; }

    /// <summary>One field per photo to keep; a photo left out is removed.</summary>
    public string?[]? KeepPhotoKeys { get; set; }
    public string? Reason { get; set; }
    public string? RowVersion { get; set; }

    /// <summary>One part per new photo: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }
}
