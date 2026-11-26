using System.Text.Json;

namespace OMTS.Infrastructure.Ocr.Gemini.Prompts;

/// <summary>
/// Prompt and schema for the sea-area licence map ("Sơ đồ khu vực biển đề nghị cấp phép nuôi trồng thủy sản"). The model only
/// copies the text of the info table and of every corner table, as strings exactly as written; the server parses the numbers and
/// checks the areas (ZoneMapNormaliser). The prompt states no rule the numbers obey and asks for no computing.
/// </summary>
internal static class ZoneMapPrompt
{
    public const string Text = """
        This photo is a Vietnamese sea-area licence map sheet. Read ONLY its tables of text. Ignore the drawing, the contours, the cross-sections, the stamps and the signatures.
        1. The info table: coordinate_system (the coordinate system, e.g. VN-2000) and central_meridian (the "kinh tuyến trục" value, e.g. 107°45'). The projection zone is not needed.
        2. Every table titled "TỌA ĐỘ CÁC ĐIỂM GÓC KHU N" (one zone per table, in the order of the sheet):
           - name: the zone name as written (e.g. "Khu 1").
           - written_area: the area written in the table title (e.g. "(Diện tích: 12,50 ha)"), as written.
           - corners: every row of the table in order: label (the point name, e.g. K1), x (the X (m) column), y (the Y (m) column), and lat and lon when the table also has latitude and longitude columns (as written, degrees-minutes-seconds or decimal), else null.
        Copy every value exactly as written, as text, keeping the dots and commas ("2.315.876,23"). Do not compute, convert, round or correct anything. A value that is not visible or not legible: null. Do not invent rows or tables.
        """;

    public static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "OBJECT",
          "properties": {
            "coordinate_system": { "type": "STRING", "nullable": true },
            "central_meridian": { "type": "STRING", "nullable": true },
            "zones": {
              "type": "ARRAY",
              "items": {
                "type": "OBJECT",
                "properties": {
                  "name": { "type": "STRING", "nullable": true },
                  "written_area": { "type": "STRING", "nullable": true },
                  "corners": {
                    "type": "ARRAY",
                    "items": {
                      "type": "OBJECT",
                      "properties": {
                        "label": { "type": "STRING", "nullable": true },
                        "x": { "type": "STRING", "nullable": true },
                        "y": { "type": "STRING", "nullable": true },
                        "lat": { "type": "STRING", "nullable": true },
                        "lon": { "type": "STRING", "nullable": true }
                      },
                      "required": ["label", "x", "y", "lat", "lon"],
                      "propertyOrdering": ["label", "x", "y", "lat", "lon"]
                    }
                  }
                },
                "required": ["name", "written_area", "corners"],
                "propertyOrdering": ["name", "written_area", "corners"]
              }
            }
          },
          "required": ["coordinate_system", "central_meridian", "zones"],
          "propertyOrdering": ["coordinate_system", "central_meridian", "zones"]
        }
        """);
}
