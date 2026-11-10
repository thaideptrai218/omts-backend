using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>EMP-API-06: the optional employee fields code, gender, ID number, email and start date, and that older clients keep working.</summary>
public partial class EmployeeProfileFieldsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Url = "/api/employees";

    [GeneratedRegex(@"^NV(\d{6,})$")]
    private static partial Regex GeneratedCode();

    private static string? CodeOf(JsonElement saved) => saved.GetProperty("employee").GetProperty("code").GetString();

    private static long NumberOf(string? code) => long.Parse(GeneratedCode().Match(code ?? string.Empty).Groups[1].Value);

    private static async Task<JsonElement> UpdateAsync(HttpClient client, Guid id, Dictionary<string, object?> body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        body["rowVersion"] = await CurrentVersionAsync(client, id);
        return await (await client.PutAsJsonAsync($"{Url}/{id}", body)).ReadAsync(expected);
    }

    [Fact]
    public async Task A_client_that_sends_none_of_the_new_fields_still_adds_an_employee_who_gets_a_code()
    {
        var (owner, zone, _) = await SetupAsync(factory);

        var first = await CreateAsync(owner, Profile($"Old client {NewCode()}", NewPhone(), zone.Id));
        var second = await CreateAsync(owner, Profile($"Old client {NewCode()}", null, zone.Id));

        Assert.Matches(GeneratedCode(), CodeOf(first));
        Assert.Equal(NumberOf(CodeOf(first)) + 1, NumberOf(CodeOf(second)));
        var employee = first.GetProperty("employee");
        foreach (var field in new[] { "gender", "idNumber", "email", "startDate" })
        {
            Assert.Equal(JsonValueKind.Null, employee.GetProperty(field).ValueKind);
        }
    }

    [Fact]
    public async Task A_typed_code_is_kept_in_upper_case_and_a_generated_one_never_meets_it()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var first = await CreateAsync(owner, Profile($"Seq {NewCode()}", null, zone.Id));
        var spent = $"NV{NumberOf(CodeOf(first)) + 1:000000}".ToLowerInvariant();

        var typed = await CreateAsync(owner, Profile($"Typed {NewCode()}", null, zone.Id, b => b["code"] = $" {spent} "));
        var generated = await CreateAsync(owner, Profile($"Gen {NewCode()}", null, zone.Id));

        Assert.Equal(spent.ToUpperInvariant(), CodeOf(typed));
        Assert.Equal(NumberOf(CodeOf(first)) + 2, NumberOf(CodeOf(generated)));
    }

    [Fact]
    public async Task A_code_taken_by_another_employee_is_refused_whatever_its_case_and_a_malformed_one_names_the_field()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var code = $"DUP-{NewCode()[1..7]}".ToUpperInvariant();
        var first = await CreateAsync(owner, Profile($"Dup {NewCode()}", null, zone.Id, b => b["code"] = code));

        var again = await owner.PostAsJsonAsync(Url, Profile($"Dup {NewCode()}", null, zone.Id, b => b["code"] = code.ToLowerInvariant()));
        var malformed = await owner.PostAsJsonAsync(Url, Profile($"Bad {NewCode()}", null, zone.Id, b => b["code"] = "NV 01"));
        var tooLong = await owner.PostAsJsonAsync(Url, Profile($"Long {NewCode()}", null, zone.Id, b => b["code"] = new string('A', 21)));

        var taken = await again.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal("Code", taken.GetProperty("field").GetString());
        Assert.Equal(IdOf(first), taken.GetProperty("existingId").GetGuid());
        Assert.Equal("Code", await malformed.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Code", await tooLong.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task An_edit_keeps_the_code_when_none_is_sent_changes_it_when_a_free_one_is_sent_and_refuses_a_taken_one()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var name = $"Edit {NewCode()}";
        var other = await CreateAsync(owner, Profile($"Other {NewCode()}", null, zone.Id));
        var created = await CreateAsync(owner, Profile(name, null, zone.Id));
        var id = IdOf(created);

        var unchanged = await UpdateAsync(owner, id, Profile(name, null, zone.Id));
        var renamed = await UpdateAsync(owner, id, Profile(name, null, zone.Id, b => b["code"] = "ALPHA-1"));
        var same = await UpdateAsync(owner, id, Profile(name, null, zone.Id, b => b["code"] = "alpha-1"));
        var taken = await UpdateAsync(owner, id, Profile(name, null, zone.Id, b => b["code"] = CodeOf(other)), HttpStatusCode.Conflict);

        Assert.Equal(CodeOf(created), CodeOf(unchanged));
        Assert.Equal("ALPHA-1", CodeOf(renamed));
        Assert.Equal("ALPHA-1", CodeOf(same));
        Assert.Equal("MSG-GEN-02", taken.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_new_fields_are_saved_and_returned_to_those_who_maintain_the_employee_only()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var elsewhere = await factory.AddZoneAsync(NewCode());
        var own = await ManagerAsync(factory, zone);
        var outsider = await ManagerAsync(factory, elsewhere);
        var today = EmployeeRequests.Today();
        var name = $"Fields {NewCode()}";

        var created = await CreateAsync(owner, Profile(name, null, zone.Id, b =>
        {
            b["gender"] = "Female";
            b["idNumber"] = "079090001234";
            b["email"] = "Lan.Nguyen@Example.com";
            b["startDate"] = today.AddDays(-10).ToString("yyyy-MM-dd");
        }));
        var id = IdOf(created);

        var byOwner = created.GetProperty("employee");
        Assert.Equal("Female", byOwner.GetProperty("gender").GetString());
        Assert.Equal("079090001234", byOwner.GetProperty("idNumber").GetString());
        Assert.Equal("lan.nguyen@example.com", byOwner.GetProperty("email").GetString());
        Assert.Equal(today.AddDays(-10).ToString("yyyy-MM-dd"), byOwner.GetProperty("startDate").GetString());

        var ownDetail = await (await own.GetAsync($"{Url}/{id}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("079090001234", ownDetail.GetProperty("idNumber").GetString());

        // Another zone sees the employee and the code, and none of the personal fields.
        var outsiderDetail = await (await outsider.GetAsync($"{Url}/{id}")).ReadAsync(HttpStatusCode.OK);
        Assert.False(outsiderDetail.GetProperty("canMaintain").GetBoolean());
        Assert.Equal(CodeOf(created), outsiderDetail.GetProperty("code").GetString());
        foreach (var field in new[] { "gender", "idNumber", "email", "startDate" })
        {
            Assert.Equal(JsonValueKind.Null, outsiderDetail.GetProperty(field).ValueKind);
        }

        // The list carries the code and never the ID number.
        var item = (await (await owner.GetAsync($"{Url}?search={Uri.EscapeDataString(name)}")).ReadAsync(HttpStatusCode.OK)).GetProperty("items")[0];
        Assert.Equal(CodeOf(created), item.GetProperty("code").GetString());
        Assert.False(item.TryGetProperty("idNumber", out _));
    }

    [Fact]
    public async Task The_audit_log_keeps_the_new_fields_but_says_only_whether_an_id_number_is_held()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var created = await CreateAsync(owner, Profile($"Audit {NewCode()}", null, zone.Id, b => { b["idNumber"] = "079090004321"; b["email"] = "audit@example.com"; }));
        var id = IdOf(created);
        await UpdateAsync(owner, id, Profile($"Audit {NewCode()}", null, zone.Id, b => b["gender"] = "Male"));

        var events = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id).ToListAsync());

        Assert.All(events, e => Assert.DoesNotContain("079090004321", (e.Before ?? string.Empty) + e.After));
        var createdEvent = events.Single(e => e.Action == "Employee.Created");
        Assert.Contains(CodeOf(created)!, createdEvent.After);
        Assert.Contains("audit@example.com", createdEvent.After);
    }

    [Fact]
    public async Task An_edit_that_leaves_the_fields_out_keeps_them_and_an_empty_text_clears_the_id_number_and_the_email()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var name = $"Clear {NewCode()}";
        var id = IdOf(await CreateAsync(owner, Profile(name, null, zone.Id, b =>
        {
            b["gender"] = "Other";
            b["idNumber"] = "079090009999";
            b["email"] = "keep@example.com";
            b["startDate"] = "2026-01-05";
        })));

        var kept = (await UpdateAsync(owner, id, Profile(name, null, zone.Id))).GetProperty("employee");
        var cleared = (await UpdateAsync(owner, id, Profile(name, null, zone.Id, b => { b["idNumber"] = ""; b["email"] = " "; }))).GetProperty("employee");

        Assert.Equal(("Other", "079090009999", "keep@example.com", "2026-01-05"),
            (kept.GetProperty("gender").GetString(), kept.GetProperty("idNumber").GetString(), kept.GetProperty("email").GetString(), kept.GetProperty("startDate").GetString()));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("idNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("email").ValueKind);
        Assert.Equal("Other", cleared.GetProperty("gender").GetString());
        Assert.Equal("2026-01-05", cleared.GetProperty("startDate").GetString());
    }

    [Theory]
    [InlineData("idNumber", "12345678", "ID number")]
    [InlineData("idNumber", "1234567890123", "ID number")]
    [InlineData("idNumber", "07909000123x", "ID number")]
    [InlineData("email", "not an email", "Email")]
    [InlineData("email", "a@b", "Email")]
    public async Task An_invalid_value_of_a_new_field_is_refused_naming_the_field(string property, string value, string field)
    {
        var (owner, zone, _) = await SetupAsync(factory);

        var response = await owner.PostAsJsonAsync(Url, Profile($"Invalid {NewCode()}", null, zone.Id, b => b[property] = value));

        Assert.Equal(field, await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task A_start_date_in_the_future_a_gender_that_does_not_exist_and_an_email_over_254_characters_are_refused()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var longEmail = $"{new string('a', 64)}@{new string('b', 63)}.{new string('c', 63)}.{new string('d', 63)}.com";

        var future = await owner.PostAsJsonAsync(Url, Profile($"Future {NewCode()}", null, zone.Id, b => b["startDate"] = EmployeeRequests.Today().AddDays(1).ToString("yyyy-MM-dd")));
        var gender = await owner.PostAsJsonAsync(Url, Profile($"Gender {NewCode()}", null, zone.Id, b => b["gender"] = "Alien"));
        var email = await owner.PostAsJsonAsync(Url, Profile($"Email {NewCode()}", null, zone.Id, b => b["email"] = longEmail));
        var today = await owner.PostAsJsonAsync(Url, Profile($"Today {NewCode()}", null, zone.Id, b => b["startDate"] = EmployeeRequests.Today().ToString("yyyy-MM-dd")));

        Assert.Equal("Start date", await future.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await gender.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal("Email", await email.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(HttpStatusCode.Created, today.StatusCode);
    }
}
