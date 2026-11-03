using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// QA of the LAB-01 unlink (OMTS-214): relinking rules after an unlink, audit parity with a link, the base64 row
/// version in the query string, and parallel unlinks / edits on SQL Server.
/// </summary>
public class EmployeeUnlinkQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // 0xFB 0xFF ends in "+/8=": both characters a query string changes or splits unless they are URL-encoded.
    private static readonly byte[] PlusSlashVersion = [0, 0, 0, 0, 0, 0, 0xFB, 0xFF];
    private const string PlusSlashText = "AAAAAAAA+/8=";

    [Fact]
    public async Task An_unlinked_account_can_be_linked_to_another_employee_and_MSG24_still_guards_a_linked_one()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var (nameA, phoneA, nameB, phoneB) = ($"A {NewCode()}", NewPhone(), $"B {NewCode()}", NewPhone());
        var a = await CreateAsync(owner, Profile(nameA, phoneA, zone.Id, x => x["accountId"] = staff.Id));
        var b = await CreateAsync(owner, Profile(nameB, phoneB, zone.Id));

        Task<HttpResponseMessage> Link(JsonElement read, string name, string phone, Guid accountId) => owner.PutAsJsonAsync(
            $"/api/employees/{IdOf(read)}", Profile(name, phone, zone.Id, x => { x["accountId"] = accountId; x["rowVersion"] = RowVersionOf(read); }));

        var taken = await Link(b, nameB, phoneB, staff.Id);
        Assert.Equal("MSG24", await taken.ErrorCodeAsync());
        Assert.Equal(IdOf(a), (await ProblemAsync(taken)).GetProperty("existingEmployeeId").GetGuid());

        var unlinkedA = await (await owner.DeleteAsync($"/api/employees/{IdOf(a)}/account")).ReadAsync(HttpStatusCode.OK);
        var linkedB = await (await Link(b, nameB, phoneB, staff.Id)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(staff.Id, linkedB.GetProperty("employee").GetProperty("account").GetProperty("id").GetGuid());

        // A cannot take the account back while B holds it, and a trader account is never linkable.
        Assert.Equal("MSG24", await (await Link(unlinkedA, nameA, phoneA, staff.Id)).ErrorCodeAsync());
        var traderLink = await Link(unlinkedA, nameA, phoneA, trader.Id);
        Assert.Equal(HttpStatusCode.BadRequest, traderLink.StatusCode);
        Assert.Equal("Account", await FieldAsync(traderLink));
        Assert.Null(await factory.QueryAsync(db => db.Employees.Where(e => e.Id == IdOf(a)).Select(e => e.AccountId).SingleAsync()));
    }

    // Same permission (Owner) and the same audit row as a link, mirrored: Before and After swap.
    [Fact]
    public async Task Unlink_is_audited_like_a_link_and_names_the_owner_who_did_it()
    {
        await factory.EnsureCooperativeAsync();
        var ownerAccount = await factory.AddAccountAsync(NewCode(), Password);
        var owner = await factory.LoginAsync(ownerAccount.Username);
        var zone = await factory.AddZoneAsync(NewCode());
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var (name, phone) = ($"Audit {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));
        var id = IdOf(saved);
        var linked = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b =>
        {
            b["accountId"] = staff.Id;
            b["rowVersion"] = RowVersionOf(saved);
        }))).ReadAsync(HttpStatusCode.OK);
        await (await owner.DeleteAsync($"/api/employees/{id}/account?rowVersion={Uri.EscapeDataString(RowVersionOf(linked))}")).ReadAsync(HttpStatusCode.OK);

        var rows = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.RecordId == id && e.Action == "Employee.Updated").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(2, rows.Count);
        var (link, unlink) = (rows[0], rows[1]);
        Assert.Equal($"{{\"AccountId\":\"{staff.Id}\"}}", link.After);
        Assert.Equal((link.After, link.Before), (unlink.Before, unlink.After));
        Assert.All(rows, row =>
        {
            Assert.Equal(ownerAccount.Id, row.ActorAccountId);
            Assert.Equal(nameof(Employee), row.RecordType);
            Assert.Null(row.ZoneId);
            Assert.Null(row.Reason);
        });
    }

    // Base64 "+" and "/" in the query string: URL-encoded they work; a raw "+" arrives as a space, so the version
    // cannot be read (MSG01) and the link is never removed on a mangled version.
    [Fact]
    public async Task A_row_version_with_plus_and_slash_works_url_encoded_and_a_raw_plus_never_unlinks()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var id = IdOf(await CreateAsync(owner, Profile($"Base64 {NewCode()}", NewPhone(), zone.Id, b => b["accountId"] = staff.Id)));
        await factory.UpdateAsync<Employee>(id, e => e.RowVersion = PlusSlashVersion);
        Assert.Equal(PlusSlashText, Convert.ToBase64String(PlusSlashVersion));

        var raw = await owner.DeleteAsync($"/api/employees/{id}/account?rowVersion={PlusSlashText}");
        Assert.Equal(HttpStatusCode.BadRequest, raw.StatusCode);
        Assert.Equal("RowVersion", await FieldAsync(raw));
        Assert.Equal(staff.Id, await AccountIdAsync(id));

        var stale = await owner.DeleteAsync($"/api/employees/{id}/account?rowVersion={Uri.EscapeDataString("AAAAAAAA+/4=")}");
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(staff.Id, await AccountIdAsync(id));

        var unlinked = await (await owner.DeleteAsync($"/api/employees/{id}/account?rowVersion={Uri.EscapeDataString(PlusSlashText)}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("employee").GetProperty("account").ValueKind);
        Assert.Null(await AccountIdAsync(id));
    }

    // A malformed version is MSG01 whether or not an account is still linked; only a well-formed one is ignored once unlinked.
    [Fact]
    public async Task A_malformed_row_version_is_refused_whether_or_not_an_account_is_linked()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var id = IdOf(await CreateAsync(owner, Profile($"Malformed {NewCode()}", NewPhone(), zone.Id, b => b["accountId"] = staff.Id)));

        var linked = await owner.DeleteAsync($"/api/employees/{id}/account?rowVersion=not-base64");
        Assert.Equal(HttpStatusCode.BadRequest, linked.StatusCode);
        Assert.Equal("RowVersion", await FieldAsync(linked));
        Assert.Equal(staff.Id, await AccountIdAsync(id));

        await (await owner.DeleteAsync($"/api/employees/{id}/account")).ReadAsync(HttpStatusCode.OK);
        var unlinked = await owner.DeleteAsync($"/api/employees/{id}/account?rowVersion=not-base64");
        Assert.Equal(HttpStatusCode.BadRequest, unlinked.StatusCode);
        Assert.Equal("MSG-GEN-01", await unlinked.ErrorCodeAsync());
        Assert.Equal("RowVersion", await FieldAsync(unlinked));
        await (await owner.DeleteAsync($"/api/employees/{id}/account?rowVersion={Uri.EscapeDataString("AAAAAAAAB9E=")}")).ReadAsync(HttpStatusCode.OK);
        Assert.Single(await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id && e.Action == "Employee.Updated").ToListAsync()));
    }

    // Real row versions and real parallel requests: never a 500, never a lost or doubled change.
    [SqlServerFact]
    public async Task Parallel_unlinks_and_an_unlink_racing_an_edit_never_fail_with_500_or_lose_a_change()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        for (var round = 0; round < 5; round++)
        {
            var staff = await api.AddAccountAsync($"nv{NewCode()}", Password, SubjectType.Staff);
            var (name, phone) = ($"Song song {NewCode()}", NewPhone());
            var id = IdOf(await CreateAsync(owner, Profile(name, phone, zone.Id, b => b["accountId"] = staff.Id)));

            var both = await Task.WhenAll(owner.DeleteAsync($"/api/employees/{id}/account"), owner.DeleteAsync($"/api/employees/{id}/account"));
            await AssertOkOrMsg06Async(both, round);
            Assert.Contains(both, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Null(await QueryAsync(api, db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
            Assert.Single(await QueryAsync(api, db => db.AuditEvents
                .Where(e => e.RecordId == id && e.Action == "Employee.Updated" && e.After == "{\"AccountId\":null}").ToListAsync()));

            var current = await owner.GetFromJsonAsync<JsonElement>($"/api/employees/{id}");
            var relinked = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b =>
            {
                b["accountId"] = staff.Id;
                b["rowVersion"] = current.GetProperty("rowVersion").GetString();
            }))).ReadAsync(HttpStatusCode.OK);

            // The edit sends no accountId: it must neither undo the unlink nor be lost silently.
            var (unlink, edit) = (owner.DeleteAsync($"/api/employees/{id}/account"), owner.PutAsJsonAsync($"/api/employees/{id}",
                Profile(name, phone, zone.Id, b => { b["notes"] = "Edited"; b["rowVersion"] = RowVersionOf(relinked); })));
            var answers = await Task.WhenAll(unlink, edit);
            await AssertOkOrMsg06Async(answers, round);
            var row = await QueryAsync(api, db => db.Employees.Where(e => e.Id == id).Select(e => new { e.AccountId, e.Notes }).SingleAsync());
            Assert.True((answers[0].StatusCode == HttpStatusCode.OK) == (row.AccountId is null), $"round {round}: unlink {(int)answers[0].StatusCode}, account {row.AccountId}");
            Assert.True((answers[1].StatusCode == HttpStatusCode.OK) == (row.Notes == "Edited"), $"round {round}: edit {(int)answers[1].StatusCode}, notes {row.Notes}");
        }
    }

    private static async Task AssertOkOrMsg06Async(IEnumerable<HttpResponseMessage> answers, int round)
    {
        foreach (var answer in answers)
        {
            if (answer.StatusCode != HttpStatusCode.OK)
            {
                Assert.True(answer.StatusCode == HttpStatusCode.Conflict, $"round {round}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
                Assert.Equal("MSG06", await answer.ErrorCodeAsync());
            }
        }
    }

    private Task<Guid?> AccountIdAsync(Guid employeeId) =>
        factory.QueryAsync(db => db.Employees.Where(e => e.Id == employeeId).Select(e => e.AccountId).SingleAsync());
}
