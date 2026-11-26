namespace OMTS.Application.Features.Farming.Rafts.FormScan;

/// <summary>
/// Cleans what the model copied from the raft form: text trimmed, blank as null, each field cut at the length the raft form
/// accepts (a longer text is the model looping, not a raft). The values are never interpreted or corrected here (contract 26/11/2026).
/// </summary>
public static class RaftFormNormaliser
{
    private const int MaxNumberTextLength = 50;

    public static RaftFormReading Normalise(RaftFormReading raw) => new(
        Clean(raw.Code, SequentialCodes.RaftCodeMaxLength),
        Clean(raw.Size, 100),
        Clean(raw.LaneCount, MaxNumberTextLength),
        Clean(raw.Location, 500),
        Clean(raw.Notes, 2000),
        Clean(raw.Latitude, MaxNumberTextLength),
        Clean(raw.Longitude, MaxNumberTextLength));

    private static string? Clean(string? text, int maxLength)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
