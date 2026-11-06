using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Infrastructure.Messaging.Fakes;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223 on a real SQL Server: collation, unique names, column sizes, paging and search as the database sees them.</summary>
public class SalesCatalogueQaSqlServerTests
{
    // BR-SALE-05: unique among products, whatever the case and the spaces around it, also with every Vietnamese letter.
    [SqlServerFact]
    public async Task The_same_name_in_upper_case_or_with_spaces_around_it_is_refused()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        foreach (var name in new[] { "đặc sản ơ ư ẵ ỳ", "Hàu sữa", "Ốc hương đỏ" })
        {
            var first = await owner.CreateProductAsync(name);
            foreach (var same in new[] { name.ToUpperInvariant(), $"  {name}\t", $" {name.ToUpperInvariant()}　" })
            {
                var response = await owner.PostAsJsonAsync("/api/products", new { name = same, unitPrice = 1 });
                Assert.Equal(first.Id(), (await response.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
            }
        }
    }

    // BR-SALE-05, SALE-11.0.E2: one name on the screen. A keyboard or a paste may send decomposed letters: ơ, ư and every
    // letter built on them compare as different under SQL Server's collations, and so does the unique index.
    [SqlServerFact]
    public async Task M1_the_same_name_in_another_unicode_form_is_refused()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var answers = new List<string>();

        foreach (var name in new[] { "Hàu sữa", "Ốc hương đỏ", "Hàu thịt" })
        {
            await owner.CreateProductAsync(name);
            var response = await owner.PostAsJsonAsync("/api/products", new { name = name.Normalize(NormalizationForm.FormD), unitPrice = 1 });
            answers.Add(await response.CodeOrStatusAsync() ?? "");
        }

        Assert.Equal(["409 MSG-GEN-02", "409 MSG-GEN-02", "409 MSG-GEN-02"], answers);
    }

    // A different tone mark is a different name, not a duplicate.
    [SqlServerFact]
    public async Task Names_that_differ_by_their_diacritics_are_different_products()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;

        await owner.CreateProductAsync("Hàu sữa");
        await owner.CreateProductAsync("Hàu sưa");
        await owner.CreateProductAsync("Hau sua");
    }

    // BR-SALE-05, SALE-11.0.E2: "Hàu  sữa" and "Hàu sữa" read as one product on the screen and on an invoice.
    [SqlServerFact]
    public async Task M1_the_same_name_with_other_spacing_inside_it_is_refused()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        await owner.CreateProductAsync("Hàu sữa");
        var answers = new List<string>();

        foreach (var same in new[] { "Hàu  sữa", "Hàu sữa", "Hàu\tsữa", "Hàu​ sữa" })
        {
            var response = await owner.PostAsJsonAsync("/api/products", new { name = same, unitPrice = 1 });
            answers.Add(await response.CodeOrStatusAsync() ?? "");
        }

        Assert.Equal(["409 MSG-GEN-02", "409 MSG-GEN-02", "409 MSG-GEN-02", "409 MSG-GEN-02"], answers);
    }

    [SqlServerFact]
    public async Task A_name_cut_in_a_surrogate_pair_for_the_audit_code_or_holding_a_nul_never_answers_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;

        var emoji = await owner.CreateProductAsync(new string('a', 99) + "🦪" + NewCode());
        Assert.StartsWith(new string('a', 99), emoji.Text("name"));
        var nul = await owner.PostAsJsonAsync("/api/products", new { name = "Hàu\0nul", unitPrice = 1 });
        Assert.True((int)nul.StatusCode < 500, await nul.Content.ReadAsStringAsync());
        var customer = await owner.PostAsJsonAsync("/api/customers", new { name = "Hàu\0nul", phoneNumber = NewPhone(), address = "\0", note = "\0" });
        Assert.True((int)customer.StatusCode < 500, await customer.Content.ReadAsStringAsync());
    }

    [SqlServerFact]
    public async Task The_longest_entries_and_the_largest_price_fit_the_columns_and_the_audit()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var name = new string('Ư', 200);
        var product = await owner.CreateProductAsync(name, 999_999_999_999_999_999m);
        Assert.Equal(999_999_999_999_999_999m, product.GetProperty("unitPrice").GetDecimal());
        var read = await (await owner.GetAsync($"/api/products/{product.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((name, 999_999_999_999_999_999m), (read.Text("name"), read.GetProperty("unitPrice").GetDecimal()));
        await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name, unitPrice = 1, rowVersion = read.RowVersion() })).ReadAsync(HttpStatusCode.OK);

        var body = new { name = new string('Ệ', 200), phoneNumber = NewPhone(), address = new string('ộ', 500), note = new string('ệ', 1000) };
        var customer = (await (await owner.PostAsJsonAsync("/api/customers", body)).ReadAsync(HttpStatusCode.Created)).GetProperty("customer");
        Assert.Equal((body.name, body.address, body.note), (customer.Text("name"), customer.Text("address"), customer.Text("note")));
        await (await owner.PutAsJsonAsync($"/api/customers/{customer.Id()}", new { body.name, body.phoneNumber, address = new string('a', 500), note = new string('b', 1000), rowVersion = customer.RowVersion() })).ReadAsync(HttpStatusCode.OK);
    }

    // BR-SALE-04: rows written by registration and linking against the filtered index, through the real routes.
    [SqlServerFact]
    public async Task A_linked_profile_keeps_working_with_the_index_after_registration_and_a_new_number()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var oldNumber = NewPhone();
        var offline = await owner.CreateCustomerAsync(phone: oldNumber);
        var anonymous = host.Api.CreateClient();
        var password = NewPassword();

        var token = await SmsTokenAsync(host, anonymous, "/api/verifications", new { channel = "Sms", target = oldNumber }, oldNumber);
        await (await anonymous.PostAsJsonAsync("/api/auth/register", new { channel = "Sms", identifier = oldNumber, password, passwordConfirmation = password, verificationToken = token })).ReadAsync(HttpStatusCode.Created);
        var linked = await owner.CustomerAsync(offline.Id());
        Assert.True(linked.GetProperty("phoneLocked").GetBoolean());
        Assert.True(await host.QueryAsync(db => db.Customers.Where(c => c.Id == offline.Id()).Select(c => c.PhoneCheckedByStaff).SingleAsync()));
        var edited = await (await owner.PutAsJsonAsync($"/api/customers/{offline.Id()}", new { name = "Edited", rowVersion = linked.RowVersion() })).ReadAsync(HttpStatusCode.OK);

        var trader = host.Api.CreateClient();
        var tokens = await LoginTokensAsync(trader, oldNumber, password);
        trader.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var newNumber = NewPhone();
        var change = await SmsTokenAsync(host, trader, "/api/me/contact-changes", new { channel = "Sms", target = newNumber, currentPassword = password }, newNumber);
        await (await trader.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = change })).ReadAsync(HttpStatusCode.OK);

        Assert.False(await host.QueryAsync(db => db.Customers.Where(c => c.Id == offline.Id()).Select(c => c.PhoneCheckedByStaff).SingleAsync()));
        var moved = await owner.CustomerAsync(offline.Id());
        Assert.Equal(newNumber, moved.Text("phoneNumber"));
        Assert.NotEqual(edited.GetProperty("customer").RowVersion(), moved.RowVersion());
        var again = await owner.CreateCustomerAsync(phone: oldNumber);
        Assert.NotEqual(offline.Id(), again.Id());
        await (await owner.PutAsJsonAsync($"/api/customers/{offline.Id()}", new { name = "Edited again", rowVersion = moved.RowVersion() })).ReadAsync(HttpStatusCode.OK);
    }

    private static async Task<string> SmsTokenAsync(FarmingHost host, HttpClient client, string url, object body, string phone)
    {
        var started = await client.PostAsJsonAsync(url, body);
        var id = await VerificationIdAsync(started);
        var text = host.Api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone)[0].Text;
        return await VerifyAsync(client, id, Regex.Match(text, @"\d{6}").Value);
    }

    [SqlServerFact]
    public async Task Versions_on_sql_server_move_with_every_save_and_a_blank_or_wrong_length_one_is_refused()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var product = await owner.CreateProductAsync(unitPrice: 60_000);
        var customer = await owner.CreateCustomerAsync();

        foreach (var (version, code) in new[] { ("", "400 MSG-GEN-01"), (" ", "400 MSG-GEN-01"), ("AQ==", "409 MSG06"), (Convert.ToBase64String(new byte[64]), "409 MSG06") })
        {
            var onProduct = await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = 1, rowVersion = version });
            Assert.Equal(code, await onProduct.CodeOrStatusAsync());
            var onCustomer = await owner.PutAsJsonAsync($"/api/customers/{customer.Id()}", new { name = "X", phoneNumber = customer.Text("phoneNumber"), rowVersion = version });
            Assert.Equal(code, await onCustomer.CodeOrStatusAsync());
        }

        var off = await (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        Assert.NotEqual(product.RowVersion(), off.RowVersion());
        var on = await (await owner.PostAsync($"/api/products/{product.Id()}/resume", null)).ReadAsync(HttpStatusCode.OK);
        await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = 2, rowVersion = off.RowVersion() })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = 2, rowVersion = on.RowVersion() })).ReadAsync(HttpStatusCode.OK);
    }

    // A save that loses to the unique index: the database names the value, and the log keeps it out (NFR-01 way of the project).
    [SqlServerFact]
    public async Task A_lost_race_on_a_number_or_a_name_writes_no_number_or_name_to_the_log()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var phone = NewPhone();
        var name = $"Hàu {NewCode()}";

        var responses = await InParallelAsync(8, i => i % 2 == 0
            ? owner.PostAsJsonAsync("/api/customers", new { name = "Buyer", phoneNumber = phone })
            : owner.PostAsJsonAsync("/api/products", new { name, unitPrice = 1 }));

        await AssertAnswersAsync(responses, "201", "409 MSG-GEN-02");
        Assert.DoesNotContain(host.Api.Logs.Lines, l => l.Contains("duplicate key"));
    }

    // A save that loses to another at the very same moment: a discontinue, a price change, a rename.
    [SqlServerFact]
    public async Task An_edit_and_a_status_change_at_once_never_answer_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < 8; round++)
        {
            var product = await owner.CreateProductAsync(unitPrice: 60_000);

            var responses = await InParallelAsync(4, i => i switch
            {
                0 => owner.PostAsync($"/api/products/{product.Id()}/discontinue", null),
                1 => owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = 61_000, rowVersion = product.RowVersion() }),
                2 => owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = $"{product.Text("name")} x", unitPrice = 62_000, rowVersion = product.RowVersion() }),
                _ => owner.PostAsync($"/api/products/{product.Id()}/discontinue", null),
            });

            await AssertAnswersAsync(responses, "200", "409 MSG06", "409 MSG-GEN-09");
        }
    }
}
