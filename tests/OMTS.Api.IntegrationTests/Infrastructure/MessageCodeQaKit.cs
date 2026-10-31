using System.Net;
using System.Text.Json;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Assertions of the message-code QA (OMTS-216 PR 0.2): the RFC 7807 shape of the SRS 27/10 catalogue, where
/// <c>title</c> and <c>code</c> are the code, <c>detail</c> the English text and <c>legacyCode</c> the old code,
/// present exactly when the code changed.
/// </summary>
public static class MessageCodeQaKit
{
    /// <summary>
    /// Asserts the HTTP status, <c>status</c>, <c>title</c> = <c>code</c> = <paramref name="code"/>, and
    /// <c>legacyCode</c> = <paramref name="legacyCode"/> (absent when null). Returns the body for further checks.
    /// </summary>
    public static async Task<JsonElement> SrsProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code, string? legacyCode)
    {
        await response.Content.LoadIntoBufferAsync();
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(code, body.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()), text);
        if (legacyCode is null)
        {
            Assert.False(body.TryGetProperty("legacyCode", out _), $"No legacyCode expected for {code}: {text}");
        }
        else
        {
            Assert.Equal(legacyCode, body.GetProperty("legacyCode").GetString());
        }

        return body;
    }

    public static string Detail(this JsonElement problem) => problem.GetProperty("detail").GetString()!;

    public static string? Str(this JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The JSON body of a success response; fails the test unless the status is <paramref name="status"/>.</summary>
    public static async Task<JsonElement> BodyAsync(this HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
