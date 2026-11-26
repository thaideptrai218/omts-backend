using System.Text;
using System.Text.Json;

namespace OMTS.Infrastructure.Ocr.Qwen;

/// <summary>
/// Qwen gets the same prompt as Gemini. Gemini takes the response schema as a request field; the OpenAI-compatible endpoint of
/// the omni models does not enforce one, so the schema (the Gemini one, so both providers answer the same shape) is written
/// under the prompt as a template: <c>"key": string or null</c>, objects in braces, arrays as one example item.
/// </summary>
internal static class QwenPromptText
{
    public static string Compose(string prompt, JsonElement schema)
    {
        var text = new StringBuilder(prompt.TrimEnd());
        text.Append("\n\nAnswer with ONE JSON object and nothing else: no Markdown fence, no comment, no text before or after it. ");
        text.Append("Use exactly these keys, in this order, every key present. The template names the type of each value; null is allowed where it says \"or null\".\n");
        Write(text, schema, 0);
        return text.ToString();
    }

    private static void Write(StringBuilder text, JsonElement node, int depth)
    {
        var nullable = node.TryGetProperty("nullable", out var flag) && flag.ValueKind == JsonValueKind.True;
        var type = node.TryGetProperty("type", out var typeName) ? typeName.GetString()?.ToUpperInvariant() : null;
        var prefix = nullable && type is "OBJECT" or "ARRAY" ? "null or " : "";
        switch (type)
        {
            case "OBJECT" when node.TryGetProperty("properties", out var properties):
                text.Append(prefix).Append("{\n");
                var first = true;
                foreach (var property in Ordered(node, properties))
                {
                    text.Append(first ? "" : ",\n").Append(' ', (depth + 1) * 2).Append('"').Append(property.Name).Append("\": ");
                    Write(text, property.Value, depth + 1);
                    first = false;
                }

                text.Append('\n').Append(' ', depth * 2).Append('}');
                break;
            case "ARRAY" when node.TryGetProperty("items", out var items):
                text.Append(prefix).Append("[ ");
                Write(text, items, depth);
                text.Append(", ... ]");
                break;
            case "BOOLEAN":
                text.Append("true or false");
                break;
            case "NUMBER" or "INTEGER":
                text.Append("number").Append(nullable ? " or null" : "");
                break;
            default:
                text.Append("string").Append(nullable ? " or null" : "");
                break;
        }
    }

    /// <summary>The properties in <c>propertyOrdering</c> when the schema has it, else in the order written.</summary>
    private static IEnumerable<JsonProperty> Ordered(JsonElement node, JsonElement properties)
    {
        var all = properties.EnumerateObject().ToList();
        if (!node.TryGetProperty("propertyOrdering", out var ordering) || ordering.ValueKind != JsonValueKind.Array)
        {
            return all;
        }

        var names = ordering.EnumerateArray().Select(n => n.GetString()).ToList();
        return all.OrderBy(p => names.IndexOf(p.Name) is var i and >= 0 ? i : int.MaxValue);
    }
}
