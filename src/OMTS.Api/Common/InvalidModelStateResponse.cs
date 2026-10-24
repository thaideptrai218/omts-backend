using Microsoft.AspNetCore.Mvc;
using OMTS.Application.Common.Messages;

namespace OMTS.Api.Common;

/// <summary>
/// Request bodies or query values that cannot be read (a bad GUID, "seven" for a number, a date that is not
/// a date) answer like every other invalid field: 400 MSG01 naming the field, instead of the framework's
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
        var message = UserMessages.Invalid(field);
        var result = ProblemResults.Create(StatusCodes.Status400BadRequest, message.Code, message.Text);
        if (result.Value is ProblemDetails problem)
        {
            problem.Extensions["field"] = field;
        }

        return result;
    }

    // "$.ph" → "ph"; "$.lines[0].quantity" → "lines[0].quantity"; "request" (whole body) → "Request".
    private static string FieldName(string key)
    {
        var name = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key == "$" ? string.Empty : key;
        return name.Length == 0 ? "Request" : name;
    }
}
