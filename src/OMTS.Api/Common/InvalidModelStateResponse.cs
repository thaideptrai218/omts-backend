using Microsoft.AspNetCore.Mvc;

namespace OMTS.Api.Common;

/// <summary>
/// Request bodies or query values that cannot be read (a bad GUID, "seven" for a number, a date that is not
/// a date) answer like every other invalid field: 400 MSG-GEN-01 naming the field, instead of the framework's
/// validation body without an SRS code.
/// </summary>
internal static class InvalidModelStateResponse
{
    public static IActionResult Create(ActionContext context)
    {
        var field = context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .Select(entry => FieldName(entry.Key))
            .FirstOrDefault(name => name.Length > 0) ?? "Request";
        return ProblemResults.InvalidField(field);
    }

    // "$.ph" → "ph"; "$.lines[0].quantity" → "lines[0].quantity"; "request" (whole body) → "Request".
    private static string FieldName(string key)
    {
        var name = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key == "$" ? string.Empty : key;
        return name.Length == 0 ? "Request" : name;
    }
}
