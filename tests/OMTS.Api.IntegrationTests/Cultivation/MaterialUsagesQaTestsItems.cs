using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of the items and values of BR-CYCLE-03: the catalogue rule of BR-PURCHASE-10, Discontinued items, quantity and reason limits, one record per item, malformed requests.</summary>
public class MaterialUsagesQaTestsItems(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> PutRawAsync(HttpClient client, string path, string json) =>
        client.PutAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static object ItemBody(JsonElement item, string? group = null, string? unit = null) => new
    {
        code = item.Text("code"),
        name = item.Text("name"),
        group = group ?? item.Text("group"),
        unit = unit ?? item.Text("unit"),
        rowVersion = item.Text("rowVersion"),
    };

    private static async Task<JsonElement> ReadItemAsync(HttpClient owner, Guid itemId) =>
        await owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{itemId}");

    // ---- H1: BR-PURCHASE-10 and the data rule "Input item group and unit"; design §1.3 asks it of this PR. ----

    [Theory]
    [InlineData("group", "Seed")]
    [InlineData("group", "Other")]
    [InlineData("unit", "kg")]
    public async Task H1_the_group_and_unit_of_an_item_on_a_usage_record_cannot_change(string field, string value)
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var itemId = await factory.ItemAsync();
        await owner.RecordUsageAsync(setup.RaftId, itemId, quantity: 4m);
        var item = await ReadItemAsync(owner, itemId);

        var answer = await owner.PutAsJsonAsync($"/api/farming-input-items/{itemId}", field == "group" ? ItemBody(item, group: value) : ItemBody(item, unit: value));

        var text = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.StatusCode == HttpStatusCode.BadRequest, $"The {field} of an item on a usage record changed to {value}: {(int)answer.StatusCode} {text}");
    }

    [Fact]
    public async Task H1_an_item_on_a_usage_record_shows_as_used()
    {
        var setup = await factory.CycleAsync();
        var itemId = await factory.ItemAsync();
        await setup.Owner.Client.RecordUsageAsync(setup.RaftId, itemId);

        Assert.True((await ReadItemAsync(setup.Owner.Client, itemId)).Flag("isUsed"));
    }

    // What H1 does to the records: the history says something else, and a record holds an item of no group Material, with no way to see it was ever different.
    [Fact]
    public async Task H1_a_change_of_unit_or_group_does_not_rewrite_what_a_record_says()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var itemId = await factory.ItemAsync();
        var record = await owner.RecordUsageAsync(setup.RaftId, itemId, quantity: 4m);
        var item = await ReadItemAsync(owner, itemId);

        await owner.PutAsJsonAsync($"/api/farming-input-items/{itemId}", ItemBody(item, group: "Seed", unit: "kg"));

        var group = await factory.QueryAsync(db => db.MaterialUsages.Where(u => u.Id == record.Id()).Select(u => u.Item.Group).SingleAsync());
        Assert.Equal((record.Text("unit"), MaterialUsage.ItemGroup), ((await owner.GetUsageAsync(record.Id())).Text("unit"), group));
    }

    // An item on neither a receipt nor a record keeps both free: the fix must not over-reach.
    [Fact]
    public async Task An_item_on_no_record_can_still_change_group_and_unit()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var itemId = await factory.ItemAsync();
        var item = await ReadItemAsync(owner, itemId);

        var saved = await (await owner.PutAsJsonAsync($"/api/farming-input-items/{itemId}", ItemBody(item, group: "Other", unit: "kg"))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Other", "kg"), (saved.Text("group"), saved.Text("unit")));
    }

    // ---- L1: malformed requests. ----

    // CYCLE-03.0.E1: missing or invalid data answers MSG-GEN-01; the client list promises the field of the line. Never 500.
    [Fact]
    public async Task L1_a_null_line_in_items_answers_msg_gen_01_and_not_500()
    {
        var setup = await factory.CycleAsync();

        var answer = await PostRawAsync(
            setup.Owner.Client, $"/api/rafts/{setup.RaftId}/material-usages", $$"""{ "usageDate": "{{Today:yyyy-MM-dd}}", "repairReason": "x", "items": [ null ] }""");

        var text = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.StatusCode == HttpStatusCode.BadRequest, $"{(int)answer.StatusCode}: {text}");
        Assert.Equal("MSG-GEN-01", await answer.ErrorCodeAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{ \"usageDate\": \"\", \"repairReason\": \"x\", \"items\": [] }")]
    [InlineData("{ \"usageDate\": \"2026-02-30\", \"repairReason\": \"x\", \"items\": [] }")]
    [InlineData("{ \"usageDate\": \"2026-01-01T10:00:00\", \"repairReason\": \"x\", \"items\": [] }")]
    [InlineData("{ \"usageDate\": \"2026-01-01\", \"repairReason\": 5, \"items\": [] }")]
    [InlineData("{ \"usageDate\": \"2026-01-01\", \"repairReason\": \"x\", \"items\": {} }")]
    [InlineData("{ \"usageDate\": \"2026-01-01\", \"repairReason\": \"x\", \"items\": [ { \"itemId\": \"not a guid\", \"quantity\": 1 } ] }")]
    [InlineData("{ \"usageDate\": \"2026-01-01\", \"repairReason\": \"x\", \"items\": [ { \"itemId\": null, \"quantity\": \"abc\" } ] }")]
    [InlineData("{ \"usageDate\": \"2026-01-01\", \"repairReason\": \"x\", \"items\": [ { \"itemId\": null, \"quantity\": 1e40 } ] }")]
    [InlineData("{ \"usageDate\": \"2026-01-01\", \"repairReason\": \"x\", \"items\": [ {} ] }")]
    public async Task Other_malformed_records_answer_400(string json)
    {
        var setup = await factory.CycleAsync();
        var answer = await PostRawAsync(setup.Owner.Client, $"/api/rafts/{setup.RaftId}/material-usages", json);
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("MSG-GEN-01", await answer.ErrorCodeAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"raftId\": \"not a guid\" }")]
    [InlineData("{ \"usageDate\": \"yesterday\" }")]
    [InlineData("{ \"quantity\": 1e40 }")]
    [InlineData("{ }")]
    public async Task Malformed_edits_answer_400(string json)
    {
        var setup = await factory.CycleAsync();
        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());
        var answer = await PutRawAsync(setup.Owner.Client, $"/api/material-usages/{record.Id()}", json);
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("MSG-GEN-01", await answer.ErrorCodeAsync());
    }

    // The client list, "Several of these": field is the first of fields. The version is read after the fields, so it is not among them: pinned, see the report, observations.
    [Fact]
    public async Task Several_invalid_fields_are_all_listed_but_a_missing_version_only_shows_once_they_are_fixed()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());

        var post = await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(null, null, null))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Repair date", post.Text("field"));

        var bad = await owner.PutUsageAsync(record.Id(), record.AsEdit(null) with { Quantity = 0m, RowVersion = null });
        Assert.Equal(["Quantity", "Reason"], await bad.InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RowVersion = null })).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RowVersion = "not base 64!" })).InvalidFieldsAsync());
    }

    // The client list, the shapes of §1, name by name.
    [Fact]
    public async Task The_answers_have_the_shapes_of_the_client_list_and_no_price()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var itemId = await factory.ItemAsync();
        var empty = await owner.UsageListAsync(setup.RaftId);
        Assert.Equal(["canRecord", "groups", "raftCode", "raftId"], empty.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var body = new UsageBody(Today, "x", [new UsageLine(itemId)]);
        var created = await (await owner.PostUsageAsync(setup.RaftId, body)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(["message", "messageCode", "records"], created.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var record = created.GetProperty("records")[0];
        Assert.Equal(
            ["canEdit", "cycleId", "id", "itemCode", "itemId", "itemName", "quantity", "raftCode", "raftId", "recordedAt", "recordedByAccountId", "recordedByName", "repairReason", "rowVersion", "unit", "usageDate"],
            record.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), record.Text("usageDate"));

        var saved = await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 3m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["message", "messageCode", "record"], saved.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var group = (await owner.UsageListAsync(setup.RaftId)).GetProperty("groups")[0];
        Assert.Equal(["cycle", "locked", "repairs"], group.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["closeDate", "id", "rowVersion", "startDate", "status", "statusId"], group.GetProperty("cycle").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["records", "repairReason", "usageDate"], group.GetProperty("repairs")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        // The audit values of §3: codes, with these names only.
        var row = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "MaterialUsage.Created" && e.RecordId == record.Id()));
        var values = JsonDocument.Parse(row.After!).RootElement;
        Assert.Equal(["Item", "Quantity", "Raft", "RepairReason", "UsageDate"], values.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(record.Text("itemCode"), values.GetProperty("Item").GetString());
        Assert.Equal(record.Text("raftCode"), values.GetProperty("Raft").GetString());
        Assert.Equal(setup.Owner.Account.Id, row.ActorAccountId);
    }

    // CYCLE-03 has no remove, as the client list says. With a quantity above 0 required, a wrong repair can only be moved or edited: see the report, P4.
    [Fact]
    public async Task P4_there_is_no_way_to_remove_a_record()
    {
        var setup = await factory.CycleAsync();
        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await setup.Owner.Client.DeleteAsync($"/api/material-usages/{record.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.Client.PostAsJsonAsync($"/api/material-usages/{record.Id()}/remove", new { reason = "wrong" })).StatusCode);
    }

    // ---- Quantity and text limits. ----

    [Theory]
    [InlineData("0.001", "0.001")]
    [InlineData("1000000000", "1000000000")]
    [InlineData("1.2340", "1.234")]
    [InlineData("1e3", "1000")]
    public async Task A_quantity_at_the_limits_is_saved_as_given(string json, string expected)
    {
        var setup = await factory.CycleAsync();
        var body = $$"""{ "usageDate": "{{Today:yyyy-MM-dd}}", "repairReason": "x", "items": [ { "itemId": "{{await factory.ItemAsync()}}", "quantity": {{json}} } ] }""";

        var saved = await (await PostRawAsync(setup.Owner.Client, $"/api/rafts/{setup.RaftId}/material-usages", body)).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(decimal.Parse(expected), saved.GetProperty("records")[0].GetProperty("quantity").GetDecimal());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.001")]
    [InlineData("0.0005")]
    [InlineData("1.2345")]
    [InlineData("1000000000.001")]
    [InlineData("1e28")]
    public async Task A_quantity_past_the_limits_is_refused_on_record_and_on_edit(string json)
    {
        var setup = await factory.CycleAsync();
        var itemId = await factory.ItemAsync();
        var body = $$"""{ "usageDate": "{{Today:yyyy-MM-dd}}", "repairReason": "x", "items": [ { "itemId": "{{itemId}}", "quantity": {{json}} } ] }""";
        Assert.Equal(["Line 1 quantity"], await (await PostRawAsync(setup.Owner.Client, $"/api/rafts/{setup.RaftId}/material-usages", body)).InvalidFieldsAsync());

        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, itemId);
        var edit = $$"""{ "raftId": "{{setup.RaftId}}", "usageDate": "{{Today:yyyy-MM-dd}}", "itemId": "{{itemId}}", "quantity": {{json}}, "repairReason": "x", "reason": "y", "rowVersion": "{{record.Text("rowVersion")}}" }""";
        Assert.Equal(["Quantity"], await (await PutRawAsync(setup.Owner.Client, $"/api/material-usages/{record.Id()}", edit)).InvalidFieldsAsync());
    }

    [Fact]
    public async Task The_reason_and_the_repair_reason_hold_to_their_limits_after_trimming_and_stay_required_on_any_edit()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var itemId = await factory.ItemAsync();

        var longest = new string('r', 500);
        var saved = await owner.RecordUsageAsync(setup.RaftId, itemId, reason: $"  {longest}  ");
        Assert.Equal(longest, saved.Text("repairReason"));
        Assert.Equal(["Repair reason"], await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(Today, longest + "x", [new UsageLine(itemId)]))).InvalidFieldsAsync());

        foreach (var missing in new[] { null, "", "   ", "\t\n" })
        {
            Assert.Equal(["Reason"], await (await owner.PutUsageAsync(saved.Id(), saved.AsEdit(missing) with { Quantity = 3m })).InvalidFieldsAsync());
        }

        Assert.Equal(["Reason"], await (await owner.PutUsageAsync(saved.Id(), saved.AsEdit(new string('x', 1001)))).InvalidFieldsAsync());
        await (await owner.PutUsageAsync(saved.Id(), saved.AsEdit($" {new string('x', 1000)} ") with { Quantity = 3m })).ReadAsync(HttpStatusCode.OK);
        var row = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "MaterialUsage.Updated" && e.RecordId == saved.Id()));
        Assert.Equal(new string('x', 1000), row.Reason);
    }

    // ---- Items: group and status. ----

    // The client list, word for word; BR-CYCLE-03 on an edit too.
    [Fact]
    public async Task A_discontinued_item_is_refused_as_the_client_list_says_and_only_a_new_choice_is_checked()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = MaterialUsageTestKit.NewItem(statusId: StatusIds.Usage.Discontinued);
        await factory.UseDbAsync(db => db.FarmingInputItems.Add(item));

        var onRecord = await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(Today, "x", [new UsageLine(item.Id)]))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-09", "Line 1 item"), (onRecord.Text("code"), onRecord.Text("field")));
        Assert.Equal($"Input item {item.Code} is Discontinued and cannot be used for this action.", onRecord.Text("detail"));

        // Discontinued since it was recorded: a move, a new date and a new quantity keep it; leaving it and coming back is a new choice.
        var usedId = await factory.ItemAsync();
        var freeId = await factory.ItemAsync();
        var record = await owner.RecordUsageAsync(setup.RaftId, usedId);
        await factory.UpdateAsync<FarmingInputItem>(usedId, i => i.StatusId = StatusIds.Usage.Discontinued);
        var otherRaft = (await owner.CreateRaftAsync(setup.ZoneId)).Id();
        var moved = (await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = otherRaft, Quantity = 7m, UsageDate = Today.AddDays(-1) })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        var left = (await (await owner.PutUsageAsync(record.Id(), moved.AsEdit() with { ItemId = freeId })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        var back = await (await owner.PutUsageAsync(record.Id(), left.AsEdit() with { ItemId = usedId })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-09", "Item"), (back.Text("code"), back.Text("field")));
    }

    [Theory]
    [InlineData(InputItemGroup.Seed)]
    [InlineData(InputItemGroup.Other)]
    public async Task An_item_outside_the_group_material_is_refused_on_an_edit_whatever_its_status(InputItemGroup group)
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());

        Assert.Equal(["Item"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { ItemId = await factory.ItemAsync(group) })).InvalidFieldsAsync());
        Assert.Equal(["Item"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { ItemId = await factory.ItemAsync(group, StatusIds.Usage.Discontinued) })).InvalidFieldsAsync());
    }

    // BR-CYCLE-03 "one record per item", read with SCR-CYCLE-03 "by repair (date and reason)". Pinned, not graded: a question for the PM, see the report, P1.
    [Fact]
    public async Task P1_one_request_cannot_hold_an_item_twice_but_two_requests_or_an_edit_put_it_twice_in_one_repair()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var (rope, buoy) = (await factory.ItemAsync(), await factory.ItemAsync());

        Assert.Equal(["Line 2 item"], await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(Today, "same", [new UsageLine(rope), new UsageLine(rope)]))).InvalidFieldsAsync());

        await owner.RecordUsageAsync(setup.RaftId, rope, reason: "same");
        await owner.RecordUsageAsync(setup.RaftId, rope, reason: "same");
        var edited = await owner.RecordUsageAsync(setup.RaftId, buoy, reason: "same");
        await (await owner.PutUsageAsync(edited.Id(), edited.AsEdit() with { ItemId = rope })).ReadAsync(HttpStatusCode.OK);

        var repair = (await owner.UsageListAsync(setup.RaftId)).GetProperty("groups")[0].GetProperty("repairs").EnumerateArray().Single();
        Assert.Equal(3, repair.GetProperty("records").EnumerateArray().Count(r => r.GetProperty("itemId").GetGuid() == rope));
    }

    // NFR-13 asks an editing notice of every shared record but logs and water. Pinned, not graded: no module has it but water, see the report, P5.
    [Fact]
    public async Task P5_a_usage_record_has_no_editing_notice()
    {
        var setup = await factory.CycleAsync();
        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());

        Assert.False(record.TryGetProperty("editingNotice", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.Client.PostAsync($"/api/material-usages/{record.Id()}/editing", null)).StatusCode);
    }
}
