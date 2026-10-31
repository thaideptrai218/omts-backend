using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Audit.AuditSecretFlowsQaTests;
using static OMTS.Api.IntegrationTests.Audit.AuditTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>
/// QA of OMTS-216 PR 0.6: the values before and after are cleaned again when ACCOUNT-10 shows them (a row written by an
/// older version or by hand), at every depth, for every kind of secret GBR-02 and NFR-01 name.
/// </summary>
public class AuditDisplayMaskingQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Secrets_are_removed_at_every_depth_of_objects_and_arrays_and_other_values_are_kept()
    {
        var secret = NewPassword();
        var before = $$$"""
            {"Level1":[[{"Deep":{"Items":[{"PASSWORD":"{{{secret}}}","Note":"kept"}]}}]],"password_hash":"{{{secret}}}",
             "Refresh-Token":"{{{secret}}}","OTPCode":"{{{secret}}}","client.secret":"{{{secret}}}","Credential":{"Value":"{{{secret}}}"},
             "Code":"KV01","RecordCode":"KV01-B01","MustChangePassword":true,"TemporaryPasswordIssuedAt":"2026-10-31T00:00:00Z"}
            """;
        var after = $$$"""[{"AccessToken":"{{{secret}}}","Note":"one"},[{"verification_code":"{{{secret}}}"}],"plain"]""";

        var detail = await ShowAsync(before, after);

        AssertNoSecret([secret], detail.GetRawText());
        var shownBefore = detail.GetProperty("before");
        Assert.Equal(["Level1", "Code", "RecordCode", "MustChangePassword", "TemporaryPasswordIssuedAt"], shownBefore.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["Note"], shownBefore.GetProperty("Level1")[0][0].GetProperty("Deep").GetProperty("Items")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("one", detail.GetProperty("after")[0].GetProperty("Note").GetString());
        Assert.Empty(detail.GetProperty("after")[1][0].EnumerateObject());
        Assert.Equal("plain", detail.GetProperty("after")[2].GetString());
    }

    // GBR-02 and BR-LABOR-02: the 6-digit login codes of the employee app are never written to the audit log. The
    // serializer is the net for the LABOR use cases to come (LABOR-03 issues the codes), on write and on display.
    [Fact]
    public async Task Login_codes_of_the_employee_app_are_never_shown()
    {
        var code = Random.Shared.Next(100_000, 1_000_000).ToString();
        var before = $$"""{"LoginCode":"{{code}}","Employee":{"login_code":"{{code}}"},"Devices":[{"loginCode":"{{code}}"}]}""";

        var detail = await ShowAsync(before, null);

        AssertNoSecret([code], detail.GetRawText());
    }

    // A field holding several secrets, or a name written as one lower-case word, is a secret field too.
    [Fact]
    public async Task Plural_and_single_word_secret_field_names_are_never_shown()
    {
        var secret = NewPassword();
        var before = $$"""
            {"Passwords":["{{secret}}"],"RefreshTokens":["{{secret}}"],"Credentials":["{{secret}}"],"Secrets":{"a":"{{secret}}"},
             "newpassword":"{{secret}}","refreshtoken":"{{secret}}","passwordhash":"{{secret}}","Name":"kept"}
            """;

        var detail = await ShowAsync(before, null);

        AssertNoSecret([secret], detail.GetRawText());
        Assert.Equal("kept", detail.GetProperty("before").GetProperty("Name").GetString());
    }

    // "Nested JSON" also means a JSON document kept as a text value (e.g. a settings or parameters column copied whole).
    [Fact]
    public async Task A_json_document_kept_as_a_text_value_is_cleaned_too()
    {
        var secret = NewPassword();
        var inner = JsonSerializer.Serialize(new { Password = secret, Note = "kept" });
        var before = JsonSerializer.Serialize(new { Settings = inner, Name = "Owner" });

        var detail = await ShowAsync(before, null);

        AssertNoSecret([secret], detail.GetRawText());
    }

    // A hand-written row with a repeated name is still shown, without either value of a secret.
    [Fact]
    public async Task A_row_with_a_repeated_field_name_is_shown_without_its_secrets()
    {
        var (first, second) = (NewPassword(), NewPassword());
        var e = Event($"{NewTag()}-DUP", DateTimeOffset.UtcNow, "Account.Updated", "Account");
        e.Before = $$"""{"Password":"{{first}}","Password":"{{second}}","DisplayName":"A"}""";
        await factory.SeedAsync(e);
        var (_, client) = await factory.OwnerAsync();

        var response = await client.GetAsync($"/api/audit-events/{e.Id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {body}");
        AssertNoSecret([first, second], body);
    }

    private async Task<JsonElement> ShowAsync(string? before, string? after)
    {
        var e = Event($"{NewTag()}-MASK", DateTimeOffset.UtcNow, "Account.Updated", "Account");
        (e.Before, e.After) = (before, after);
        await factory.SeedAsync(e);
        var (_, client) = await factory.OwnerAsync();
        var response = await client.GetAsync($"/api/audit-events/{e.Id}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
