using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>The supplier management screen: profile fields, list figures by permission, sort, filters, history, export, import and bulk status.</summary>
public class SupplierManagementTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Url = "/api/suppliers";

    private static JsonElement[] Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToArray();

    private async Task<JsonElement> ListAsync(HttpClient client, string query) =>
        await (await client.GetAsync($"{Url}?{query}")).ReadAsync(HttpStatusCode.OK);

    private static MultipartFormDataContent Upload(byte[] bytes, string name = "suppliers.xlsx")
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(content, "file", name);
        return form;
    }

    private static byte[] Workbook(string[] headers, params object?[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Suppliers");
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = sheet.Cell(r + 2, c + 1);
                switch (rows[r][c])
                {
                    case double number:
                        cell.Value = number;
                        break;
                    case string text when text.StartsWith("FORMULA:", StringComparison.Ordinal):
                        cell.FormulaA1 = text["FORMULA:".Length..];
                        break;
                    case string text:
                        cell.Value = text;
                        break;
                }
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static readonly string[] ImportHeaders = ["Name", "Group", "Phone", "Email", "Note"];

    [Fact]
    public async Task A_new_supplier_gets_a_generated_code_and_keeps_the_profile_fields()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var created = await (await owner.PostAsJsonAsync(Url, new
        {
            name = "Trại giống", supplierType = "Seed", phoneNumber = NewPhone(), province = "Quảng Ninh", ward = "Cái Rồng", note = "Giao sáng",
            companyName = "Công ty A", taxCode = "0101234567-001", identityNumber = "012345678901",
        })).ReadAsync(HttpStatusCode.Created);

        Assert.Matches("^NCC\\d{6,}$", created.GetProperty("code").GetString()!);
        Assert.Equal("Quảng Ninh", created.GetProperty("province").GetString());
        Assert.Equal("012345678901", created.GetProperty("identityNumber").GetString());
        Assert.NotEqual(default, created.GetProperty("createdAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("taxCode", "12345", "Tax code")]
    [InlineData("identityNumber", "123", "Identity number")]
    public async Task A_badly_formed_tax_code_or_identity_number_is_refused(string property, string value, string field)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var body = new Dictionary<string, object?> { ["name"] = "S", ["supplierType"] = "Seed", ["phoneNumber"] = NewPhone(), [property] = value };

        Assert.Equal(field, await (await owner.PostAsJsonAsync(Url, body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task An_update_from_a_client_that_does_not_know_the_new_fields_keeps_them_and_an_empty_text_clears_them()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();
        var created = await (await owner.PostAsJsonAsync(Url, new { name = "S", supplierType = "Seed", phoneNumber = phone, companyName = "Công ty A", note = "Ghi chú" })).ReadAsync(HttpStatusCode.Created);

        var kept = await (await owner.PutAsJsonAsync($"{Url}/{created.Id()}", new { name = "S2", supplierType = "Seed", phoneNumber = phone, rowVersion = created.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Công ty A", kept.GetProperty("companyName").GetString());

        var cleared = await (await owner.PutAsJsonAsync($"{Url}/{created.Id()}", new { name = "S2", supplierType = "Seed", phoneNumber = phone, companyName = "", rowVersion = kept.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("companyName").ValueKind);
        Assert.Equal("Ghi chú", cleared.GetProperty("note").GetString());
    }

    [Fact]
    public async Task The_identity_number_never_reaches_the_list_the_audit_log_or_the_export()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var tag = NewCode();
        var created = await (await owner.PostAsJsonAsync(Url, new { name = $"{tag} S", supplierType = "Seed", phoneNumber = NewPhone(), identityNumber = "098765432101" })).ReadAsync(HttpStatusCode.Created);

        var row = Items(await ListAsync(owner, $"search={tag}")).Single();
        Assert.False(row.TryGetProperty("identityNumber", out _));
        Assert.Equal("098765432101", (await owner.GetFromJsonAsync<JsonElement>($"{Url}/{created.Id()}")).GetProperty("identityNumber").GetString());

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == created.Id()).Select(e => e.After).ToListAsync());
        Assert.DoesNotContain(audits, json => json != null && json.Contains("098765432101", StringComparison.Ordinal));

        var export = await (await owner.GetAsync($"{Url}/export?search={tag}")).Content.ReadAsByteArrayAsync();
        using var workbook = new XLWorkbook(new MemoryStream(export));
        Assert.DoesNotContain(workbook.Worksheets.First().CellsUsed(), c => c.GetString().Contains("098765432101", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_zone_manager_sees_total_purchased_but_never_the_debt()
    {
        var world = await factory.WorldAsync();
        var tag = NewCode();
        world = world with { SupplierId = await world.Owner.CreateSupplierAsync(name: $"{tag} NCC") };
        await world.CompletedAsync(new LineBody(world.Other, 2, 50_000));
        var manager = await factory.ZoneManagerAsync(world.Zone);

        var row = Items(await ListAsync(manager, $"search={tag}")).Single();

        Assert.Equal(100_000m, row.Money("totalPurchased"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("currentDebt").ValueKind);
    }

    [Theory]
    [InlineData("sortBy=currentDebt")]
    [InlineData("currentDebtFrom=1")]
    [InlineData("currentDebtTo=1")]
    public async Task Sorting_or_filtering_on_debt_without_the_permission_is_refused(string query)
    {
        var (zone, _) = await factory.ZoneWithRaftAsync();
        var manager = await factory.ZoneManagerAsync(zone);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{Url}?{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{Url}/export?{query}")).StatusCode);
    }

    [Fact]
    public async Task An_accountant_reads_both_figures_and_the_list_debt_equals_the_debt_screen()
    {
        var world = await factory.WorldAsync();
        var tag = NewCode();
        world = world with { SupplierId = await world.Owner.CreateSupplierAsync(name: $"{tag} NCC") };
        await world.CompletedAsync(new LineBody(world.Other, 2, 50_000));
        await world.PayAsync(30_000);
        var accountant = await factory.AccountantAsync();

        var row = Items(await ListAsync(accountant, $"search={tag}")).Single();

        Assert.Equal(await world.DebtAsync(), row.Money("currentDebt"));
        Assert.Equal(70_000m, row.Money("currentDebt"));
        Assert.Equal(100_000m, row.Money("totalPurchased"));
    }

    [Fact]
    public async Task The_export_of_a_zone_manager_has_no_debt_column_and_the_accountant_may_export()
    {
        var world = await factory.WorldAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);

        var file = await (await manager.GetAsync($"{Url}/export")).Content.ReadAsByteArrayAsync();
        using var workbook = new XLWorkbook(new MemoryStream(file));
        var headers = workbook.Worksheets.First().Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("Total purchased", headers);
        Assert.DoesNotContain("Current debt", headers);

        var accountant = await factory.AccountantAsync();
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"{Url}/export")).StatusCode);
    }

    [Fact]
    public async Task A_name_that_looks_like_a_formula_is_exported_as_plain_text()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var tag = NewCode();
        var name = $"=HYPERLINK(\"http://x\",\"{tag}\")";
        await owner.CreateSupplierAsync(name: name);

        var file = await (await owner.GetAsync($"{Url}/export?search={tag}")).Content.ReadAsByteArrayAsync();
        using var workbook = new XLWorkbook(new MemoryStream(file));
        var cell = workbook.Worksheets.First().Cell(2, 2);

        Assert.False(cell.HasFormula);
        Assert.Equal(name, cell.GetString());
    }

    [Theory]
    [InlineData("sortBy=password")]
    [InlineData("sortDir=sideways")]
    [InlineData("totalPurchasedFrom=5&totalPurchasedTo=1")]
    [InlineData("currentDebtFrom=5&currentDebtTo=1")]
    [InlineData("purchaseFrom=2026-02-01&purchaseTo=2026-01-01")]
    public async Task An_unknown_sort_or_a_reversed_range_is_invalid(string query)
    {
        var owner = await factory.PurchasingOwnerAsync();

        await (await owner.GetAsync($"{Url}?{query}")).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task Sorting_and_range_filters_on_the_figures_cover_every_supplier_across_pages()
    {
        var world = await factory.WorldAsync();
        var owner = world.Owner;
        var tag = NewCode();
        var ids = new List<Guid>();
        foreach (var quantity in new[] { 3, 1, 2 })
        {
            var id = await owner.CreateSupplierAsync(name: $"{tag} {quantity}");
            await (world with { SupplierId = id }).CompletedAsync(new LineBody(world.Other, quantity, 10_000));
            ids.Add(id);
        }

        var first = Items(await ListAsync(owner, $"search={tag}&sortBy=totalPurchased&sortDir=desc&page=1&pageSize=2"));
        var second = Items(await ListAsync(owner, $"search={tag}&sortBy=totalPurchased&sortDir=desc&page=2&pageSize=2"));
        Assert.Equal([30_000m, 20_000m], first.Select(r => r.Money("totalPurchased")));
        Assert.Equal([10_000m], second.Select(r => r.Money("totalPurchased")));

        var ranged = await ListAsync(owner, $"search={tag}&totalPurchasedFrom=15000&currentDebtTo=25000");
        Assert.Equal(1, ranged.GetProperty("total").GetInt32());
        Assert.Equal(20_000m, Items(ranged).Single().Money("currentDebt"));
    }

    [Fact]
    public async Task A_purchase_period_limits_the_suppliers_and_the_total_to_that_period()
    {
        var world = await factory.WorldAsync();
        var tag = NewCode();
        var id = await world.Owner.CreateSupplierAsync(name: $"{tag} A");
        var buyer = world with { SupplierId = id };
        await world.Owner.CreateReceiptAsync(buyer.Body(new LineBody(world.Other, 1, 10_000)) with { ReceiptDate = Today.AddDays(-30), Complete = true });
        await world.Owner.CreateReceiptAsync(buyer.Body(new LineBody(world.Other, 1, 4_000)) with { ReceiptDate = Today, Complete = true });
        await world.Owner.CreateSupplierAsync(name: $"{tag} B");

        var row = Items(await ListAsync(world.Owner, $"search={tag}&purchaseFrom={Today.AddDays(-7):yyyy-MM-dd}&purchaseTo={Today:yyyy-MM-dd}")).Single();

        Assert.Equal(id, row.Id());
        Assert.Equal(4_000m, row.Money("totalPurchased"));
    }

    [Fact]
    public async Task The_history_lists_receipts_and_returns_newest_first_for_those_who_view_receipts()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 2, 10_000));
        await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);

        var history = await (await world.Owner.GetAsync($"{Url}/{world.SupplierId}/history?pageSize=1")).ReadAsync(HttpStatusCode.OK);
        var page2 = await (await world.Owner.GetAsync($"{Url}/{world.SupplierId}/history?page=2&pageSize=1")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(2, history.GetProperty("total").GetInt32());
        Assert.Equal(("Return", 10_000m), (Items(history).Single().GetProperty("kind").GetString(), Items(history).Single().Money("total")));
        Assert.Equal(("Receipt", 20_000m), (Items(page2).Single().GetProperty("kind").GetString(), Items(page2).Single().Money("total")));
        Assert.Equal(receipt.Id(), Items(history).Single().GetProperty("receiptId").GetGuid());
        Assert.False(string.IsNullOrEmpty(Items(page2).Single().GetProperty("createdBy").GetString()));
        await (await world.Owner.GetAsync($"{Url}/{Guid.NewGuid()}/history")).ReadAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_valid_file_imports_every_row_with_codes_and_restores_a_phone_stored_as_a_number()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (first, second) = (NewPhone(), NewPhone());
        var file = Workbook(ImportHeaders, [$"Nhập {first}", "Giống", first, "a@example.com", "Ghi chú"], [$"Nhập {second}", "Material", double.Parse(second[1..]), null, null]);

        var result = await (await owner.PostAsync($"{Url}/import", Upload(file))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(2, result.GetProperty("imported").GetInt32());
        Assert.Empty(result.GetProperty("errors").EnumerateArray());
        var saved = await factory.QueryAsync(db => db.Suppliers.SingleAsync(s => s.PhoneNumber == second));
        Assert.Matches("^NCC\\d{6,}$", saved.Code);
    }

    [Fact]
    public async Task One_bad_row_refuses_the_whole_file_and_reports_each_row()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var existing = NewPhone();
        await owner.CreateSupplierAsync(phone: existing);
        var good = NewPhone();
        var before = await factory.QueryAsync(db => db.Suppliers.CountAsync());
        var file = Workbook(ImportHeaders, ["Tốt", "Seed", good], ["Lặp", "Seed", good], ["Đã có", "Seed", existing], [null, "Nope", "abc"]);

        var result = await (await owner.PostAsync($"{Url}/import", Upload(file))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(0, result.GetProperty("imported").GetInt32());
        Assert.Equal([3, 4, 5], result.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("row").GetInt32()));
        Assert.Equal(before, await factory.QueryAsync(db => db.Suppliers.CountAsync()));
    }

    [Fact]
    public async Task A_formula_cell_is_refused_and_never_evaluated()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var file = Workbook(ImportHeaders, ["FORMULA:1+1", "Seed", NewPhone()]);

        var result = await (await owner.PostAsync($"{Url}/import", Upload(file))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(2, result.GetProperty("errors")[0].GetProperty("row").GetInt32());
        Assert.Equal(0, result.GetProperty("imported").GetInt32());
    }

    [Fact]
    public async Task A_file_that_is_not_a_workbook_too_large_or_with_too_many_rows_is_refused()
    {
        var owner = await factory.PurchasingOwnerAsync();
        Assert.Equal("File", await (await owner.PostAsync($"{Url}/import", Upload("not a workbook"u8.ToArray()))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("File", await (await owner.PostAsync($"{Url}/import", Upload(new byte[2 * 1024 * 1024 + 1]))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var many = Workbook(ImportHeaders, Enumerable.Range(0, 1001).Select(i => new object?[] { $"S{i}", "Seed", "0900000000" }).ToArray());
        Assert.Equal("File", await (await owner.PostAsync($"{Url}/import", Upload(many))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("File", await (await owner.PostAsync($"{Url}/import", Upload(Workbook(["Other"], ["x"])))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task The_template_has_the_import_columns_and_import_needs_create_permission()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var template = await (await owner.GetAsync($"{Url}/import/template")).Content.ReadAsByteArrayAsync();
        using var workbook = new XLWorkbook(new MemoryStream(template));
        Assert.Contains("Phone", workbook.Worksheets.First().Row(1).CellsUsed().Select(c => c.GetString()));

        var accountant = await factory.AccountantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsync($"{Url}/import", Upload(template))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync($"{Url}/import/template")).StatusCode);
    }

    [Fact]
    public async Task Bulk_status_reports_each_id_and_keeps_the_rule_for_each_row()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (active, stopped, missing) = (await owner.CreateSupplierAsync(), await owner.CreateSupplierAsync(), Guid.NewGuid());
        await (await owner.PostAsync($"{Url}/{stopped}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        var result = await (await owner.PostAsJsonAsync($"{Url}/bulk-status", new { ids = new[] { active, stopped, missing }, action = "Discontinue" })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((1, 2), (result.GetProperty("succeeded").GetInt32(), result.GetProperty("failed").GetInt32()));
        var outcomes = result.GetProperty("outcomes").EnumerateArray().ToDictionary(o => o.Id(), o => o.GetProperty("succeeded").GetBoolean());
        Assert.True(outcomes[active]);
        Assert.False(outcomes[stopped]);
        Assert.False(outcomes[missing]);
        Assert.Equal(["Supplier.Created", "Supplier.Discontinued"], await factory.AuditActionsAsync(active));
    }

    [Fact]
    public async Task Bulk_status_needs_edit_permission_and_a_sensible_id_list()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var accountant = await factory.AccountantAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"{Url}/bulk-status", new { ids = new[] { Guid.NewGuid() }, action = "Resume" })).StatusCode);
        await (await owner.PostAsJsonAsync($"{Url}/bulk-status", new { ids = Array.Empty<Guid>(), action = "Resume" })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PostAsJsonAsync($"{Url}/bulk-status", new { ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()), action = "Resume" })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }
}
