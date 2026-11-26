using System.Text.Json;

namespace OMTS.Infrastructure.Ocr.Gemini.Prompts;

/// <summary>
/// The meter prompt and response schema of the spike (omts-plans 261119-ocr-spike/scripts/gemini_meter.py), word for word: the
/// 13/14 result of the full-photo test was measured with exactly these. The schema is the Gemini one the SDK sends: upper-case
/// types and <c>propertyOrdering</c>. <c>other_numbers</c> gives the model a place for model numbers and settings; it is ignored.
/// </summary>
internal static class MeterPrompt
{
    public const string Text = """
        This photo shows the display of a water quality meter (pH, salinity, conductivity, TDS or temperature).
        Read the values shown on the display.
        - value: the number exactly as displayed, digits and decimal point only, no unit, no conversion (keep °F as shown).
        - ph: the pH reading. salinity: the salinity reading, or the conductivity/TDS reading when the meter shows no salinity.
          temperature: the temperature reading.
        - A field the display does not show: null. Never guess a number that is not visible.
        - uncertain: true when a digit is hard to read or when no label/unit on the display tells which field it is.
        - measured_at: the date/time shown on the display, if any, else null.
        - other_numbers: any other numbers on the display or device (model numbers, settings), as text.
        """;

    public static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "OBJECT",
          "properties": {
            "ph": { "type": "OBJECT", "nullable": true, "properties": { "value": { "type": "STRING", "nullable": true }, "uncertain": { "type": "BOOLEAN" } }, "required": ["value", "uncertain"], "propertyOrdering": ["value", "uncertain"] },
            "salinity": { "type": "OBJECT", "nullable": true, "properties": { "value": { "type": "STRING", "nullable": true }, "uncertain": { "type": "BOOLEAN" } }, "required": ["value", "uncertain"], "propertyOrdering": ["value", "uncertain"] },
            "temperature": { "type": "OBJECT", "nullable": true, "properties": { "value": { "type": "STRING", "nullable": true }, "uncertain": { "type": "BOOLEAN" } }, "required": ["value", "uncertain"], "propertyOrdering": ["value", "uncertain"] },
            "measured_at": { "type": "STRING", "nullable": true },
            "other_numbers": { "type": "ARRAY", "items": { "type": "STRING" } }
          },
          "required": ["ph", "salinity", "temperature", "measured_at", "other_numbers"],
          "propertyOrdering": ["ph", "salinity", "temperature", "measured_at", "other_numbers"]
        }
        """);
}
