namespace OMTS.Api.Controllers;

/// <summary>Multipart fields of a lane damage report: the note of what happened (when, which lane, how), the optional category, the raft version and 1 to 3 on-site photos (owner decision of 12/11/2026).</summary>
public sealed class ReportLaneDamageForm
{
    public string? Reason { get; set; }

    public string? ReasonCode { get; set; }

    /// <summary>The raft version. An empty value stays empty (not null), as it did in the JSON body this form replaces: the check then answers on the version itself.</summary>
    [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? RowVersion { get; set; }

    /// <summary>One part per photo, 1 to 3: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }
}
