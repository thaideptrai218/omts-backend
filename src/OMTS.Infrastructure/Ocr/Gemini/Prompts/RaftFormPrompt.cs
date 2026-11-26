using System.Text.Json;

namespace OMTS.Infrastructure.Ocr.Gemini.Prompts;

/// <summary>
/// Prompt and schema for the raft form. The labels are those of the form the mobile app read with Tesseract before (raftFormOcrService.ts).
/// Every field is copied as written, as text; the app validates the values as it does for a form typed by hand.
/// </summary>
internal static class RaftFormPrompt
{
    public const string Text = """
        This photo is a paper form describing one raft (bè) of an oyster farm, in Vietnamese. Each field is a label followed by the written value.
        Read these fields:
        - code: the value after "Mã bè" (or "Mã số bè").
        - size: the value after "Kích thước".
        - lane_count: the value after "Số hàng" (or "Số hàng bè").
        - location: the value after "Vị trí".
        - notes: the value after "Ghi chú" or "Tình trạng".
        - latitude: the value after "Vĩ độ".
        - longitude: the value after "Kinh độ".
        Copy each value exactly as written, as text. Do not compute, convert or correct anything. A field that is not on the form or not legible: null. Never guess.
        """;

    public static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "OBJECT",
          "properties": {
            "code": { "type": "STRING", "nullable": true },
            "size": { "type": "STRING", "nullable": true },
            "lane_count": { "type": "STRING", "nullable": true },
            "location": { "type": "STRING", "nullable": true },
            "notes": { "type": "STRING", "nullable": true },
            "latitude": { "type": "STRING", "nullable": true },
            "longitude": { "type": "STRING", "nullable": true }
          },
          "required": ["code", "size", "lane_count", "location", "notes", "latitude", "longitude"],
          "propertyOrdering": ["code", "size", "lane_count", "location", "notes", "latitude", "longitude"]
        }
        """);
}
