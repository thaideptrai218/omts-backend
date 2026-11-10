namespace OMTS.Api.Controllers;

/// <summary>Multipart fields of CLAIM-01: the description and one part per photo (1 to 10, JPEG or PNG, at most 10 MB each).</summary>
public sealed class QualityClaimForm
{
    public string? Description { get; set; }

    public List<IFormFile>? Photos { get; set; }
}
