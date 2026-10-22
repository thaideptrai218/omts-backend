using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OMTS.Application.Features.Audit;

/// <summary>Keeps only the fields whose value changed, for "*.Updated" audit rows (before/after of the changed fields).</summary>
public static class AuditDiff
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Two snapshots of the same shape → the changed fields of each, or nulls when nothing changed.</summary>
    public static (JsonObject? Before, JsonObject? After) Changed(object before, object after)
    {
        var old = JsonSerializer.SerializeToNode(before, before.GetType(), Options)!.AsObject();
        var now = JsonSerializer.SerializeToNode(after, after.GetType(), Options)!.AsObject();
        var changed = now.Where(p => !SameValue(p.Value, old[p.Key])).Select(p => p.Key).ToList();
        if (changed.Count == 0)
        {
            return (null, null);
        }

        return (Pick(old, changed), Pick(now, changed));
    }

    /// <summary>Deep equality where numbers compare by value, so 21.05 and 21.050000 are the same.</summary>
    private static bool SameValue(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(p => y.ContainsKey(p.Key) && SameValue(p.Value, y[p.Key])),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(pair => SameValue(pair.First, pair.Second)),
        (JsonValue x, JsonValue y) when x.GetValueKind() == JsonValueKind.Number && y.GetValueKind() == JsonValueKind.Number =>
            x.GetValue<decimal>() == y.GetValue<decimal>(),
        _ => JsonNode.DeepEquals(a, b),
    };

    private static JsonObject Pick(JsonObject source, IEnumerable<string> keys) =>
        new(keys.Select(k => KeyValuePair.Create(k, source[k]?.DeepClone())));
}
