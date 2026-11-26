namespace OMTS.Application.Features.Farming.Rafts.FormScan;

/// <summary>
/// <c>reading</c> of <c>GET api/rafts/form-scans/{scanId}</c>, and what the model copied from the form: every field is the text as
/// written, trimmed, or null when the form has no such field or it is not legible. The app reviews and validates the fields as it
/// does for a form typed by hand (the numbers, the pin); the server does not interpret them.
/// </summary>
/// <param name="Code">"Mã bè".</param>
/// <param name="Size">"Kích thước", e.g. "24 m x 10 m".</param>
/// <param name="LaneCount">"Số hàng".</param>
/// <param name="Location">"Vị trí".</param>
/// <param name="Notes">"Ghi chú" or "Tình trạng".</param>
/// <param name="Latitude">"Vĩ độ".</param>
/// <param name="Longitude">"Kinh độ".</param>
public sealed record RaftFormReading(string? Code, string? Size, string? LaneCount, string? Location, string? Notes, string? Latitude, string? Longitude);
