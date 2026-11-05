namespace OMTS.Application.Features.WaterQuality;

/// <summary>What the record request may do with a photo key it brought back (BR-WATER-02).</summary>
public enum WaterPhotoCheck
{
    /// <summary>A stored photo of this account that no measurement uses yet.</summary>
    Usable,

    /// <summary>A measurement already uses it: possibly the record of this very operation, saved a moment ago.</summary>
    AlreadyUsed,

    /// <summary>Not the stored form of a key of this account, or no such file (never stored, or cleaned up).</summary>
    Refused,
}
