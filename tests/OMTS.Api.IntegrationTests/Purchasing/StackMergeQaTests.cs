using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Purchasing;

using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

/// <summary>QA of what the merge of the purchase stack and the raft and cycle stack could lose without a compile error: services, routes, the raft lookup and rules that read across both.</summary>
public class StackMergeQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Lookup = "/api/purchase-receipts/raft-lookup";

    // A registration lost in a resolved conflict compiles and fails at the first request: every controller and hosted service must resolve.
    [Fact]
    public void Every_controller_and_hosted_service_resolves()
    {
        using var scope = factory.Services.CreateScope();
        var failures = new List<string>();
        foreach (var controller in typeof(Program).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            foreach (var parameter in controller.GetConstructors().Single().GetParameters())
            {
                try
                {
                    scope.ServiceProvider.GetRequiredService(parameter.ParameterType);
                }
                catch (Exception failure) when (failure is InvalidOperationException)
                {
                    failures.Add($"{controller.Name}: {parameter.ParameterType.Name}: {failure.Message}");
                }
            }
        }

        Assert.Empty(failures);
        Assert.NotEmpty(scope.ServiceProvider.GetServices<IHostedService>());
    }

    // Two controllers answering one route compile and fail with a 500 at request time.
    [Fact]
    public void No_two_endpoints_share_a_method_and_a_route()
    {
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .ToList();

        Assert.Empty(routes.GroupBy(r => r, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.Contains("GET api/purchase-receipts/raft-lookup", routes);
        Assert.Contains("GET api/rafts/{raftId:guid}/lanes/{laneNumber:int}/progress", routes);
    }

    // Each record type a notification names has a name to show; what the merge kept of both sides.
    [Fact]
    public void Every_related_record_type_has_a_name_and_every_notification_type_is_unique()
    {
        var types = typeof(RelatedRecordTypes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();

        Assert.All(types, t => Assert.False(string.IsNullOrWhiteSpace(RelatedRecordTypes.ObjectName(t)), t));
        Assert.Equal("Cultivation cycle", RelatedRecordTypes.ObjectName(RelatedRecordTypes.CultivationCycle));
        Assert.Contains("CultivationLog", types);
        Assert.Contains("PurchaseReceipt", types);
        Assert.Contains("SupplierPayment", types);
        Assert.Contains("CultivationCycle", types);
        Assert.Equal(NotificationTypes.All.Count, NotificationTypes.All.Select(t => t.Code).Distinct().Count());
    }

    /// <summary>
    /// The conflict of the merge. The purchase side: receipts have no zone and follow the role only (BR-FARM-08). The raft side: the lookup of the zones of the pairs.
    /// PURCHASE-03 and SCR-PURCHASE-01 give the Zone Manager and the Accountant the raft filter, and PURCHASE-03.1 shows each line with its raft, so a person of one zone already
    /// reads the raft codes of every zone on the receipts: the filter has to offer them, or a listed receipt could not be found by its own raft. Only BR-PURCHASE-01 limits rafts, and
    /// only on a line being written.
    /// </summary>
    [Theory]
    [InlineData(StaffRole.ZoneManager)]
    [InlineData(StaffRole.Accountant)]
    public async Task The_lookup_of_a_person_of_one_zone_finds_every_raft_their_role_reads_on_receipts(StaffRole role)
    {
        var world = await factory.WorldAsync();
        var (zoneB, raftB) = await factory.ZoneWithRaftAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Material, 2m, 1_000m, RaftId: raftB.Id));
        var staff = (await factory.StaffAsync(world.Zone, role)).Client;

        var found = (await staff.GetFromJsonAsync<JsonElement>(Lookup)).EnumerateArray().Single(r => r.Id() == raftB.Id);
        Assert.Equal((raftB.Code, zoneB.Code), (found.Text("code"), found.Text("zoneCode")));

        // The same codes are on the receipt the role reads, and the filter and the material cost work with them.
        var line = (await staff.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}")).LineViewOf(world.Material).GetProperty("raft");
        Assert.Equal((raftB.Id, raftB.Code, zoneB.Code), (line.Id(), line.Text("code"), line.Text("zoneCode")));
        var listed = await staff.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?raftId={raftB.Id}");
        Assert.Contains(receipt.Id(), listed.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.Id()));
        await (await staff.GetAsync($"/api/purchase-receipts/raft-material-costs?raftId={raftB.Id}")).ReadAsync(HttpStatusCode.OK);

        // The raft itself stays closed (BR-FARM-01): a code to filter by opens nothing else.
        Assert.Equal("MSG-GEN-08", await (await staff.GetAsync($"/api/rafts/{raftB.Id}")).ErrorCodeAsync());
    }

    // BR-PURCHASE-01: on a line being written, a Zone Manager still names only the rafts In Use of their own zones. The wider lookup does not open that.
    [Fact]
    public async Task A_zone_manager_of_one_zone_cannot_name_on_a_line_a_raft_the_lookup_shows_from_another_zone()
    {
        var world = await factory.WorldAsync();
        var (_, raftB) = await factory.ZoneWithRaftAsync();
        var manager = (await factory.StaffAsync(world.Zone)).Client;
        Assert.Contains(raftB.Id, (await manager.GetFromJsonAsync<JsonElement>(Lookup)).EnumerateArray().Select(r => r.Id()));

        var refused = await manager.PostReceiptAsync(new ReceiptBody { SupplierId = world.SupplierId, Complete = true, Lines = [new LineBody(world.Material, 1m, 1m, RaftId: raftB.Id)] });

        Assert.False(refused.IsSuccessStatusCode, await refused.Content.ReadAsStringAsync());
        await manager.CreateReceiptAsync(new ReceiptBody { SupplierId = world.SupplierId, Complete = true, Lines = [new LineBody(world.Material, 1m, 1m, RaftId: world.Raft.Id)] });
    }

    // §I.4.4: PURCHASE-03 R for the Accountant, No on PURCHASE-01, -02 and -06, the rules the removed PurchasingFlowTests held for zones, now by role.
    [Fact]
    public async Task An_accountant_only_reads_receipts_and_a_zone_manager_is_refused_the_debts_and_payments()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 2m, 1_000m));
        var line = receipt.LineOf(world.Seed);
        var accountant = (await factory.StaffAsync(world.Zone, StaffRole.Accountant)).Client;
        var manager = (await factory.StaffAsync(world.Zone)).Client;

        await (await accountant.GetAsync($"/api/purchase-receipts/{receipt.Id()}")).ReadAsync(HttpStatusCode.OK);
        foreach (var refused in new[]
        {
            await accountant.PostReceiptAsync(new ReceiptBody { SupplierId = world.SupplierId, Lines = [new LineBody(world.Seed, 1m, 1m)] }),
            await accountant.PostReturnAsync(receipt.Id(), new ReturnBody { OwnerApproved = true, Lines = [new ReturnLineBody(line, 1m)] }),
            await accountant.CancelAsync(receipt.Id(), "Wrong"),
            await accountant.CorrectAsync(receipt.Id(), "Wrong", new { lineId = line, unitPrice = 1 }),
            await manager.GetAsync("/api/supplier-debts"),
            await manager.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1m }),
        })
        {
            Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        }
    }

    // BR-PURCHASE-01 "a receipt has no photo of the supplier's invoice": the row OversizedUploadQaTests lost by the merge is for a route that is really gone.
    [Fact]
    public async Task The_route_of_invoice_photos_is_gone_so_the_oversized_upload_test_had_nothing_to_keep()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1m, 1m));

        var answer = await world.Owner.PostAsync($"/api/purchase-receipts/{receipt.Id()}/invoice-photos", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    // BR-FARM-05: the raft view reads the material used on the raft; purchase lines that name the raft are not its cost, and seeding changes nothing.
    [Fact]
    public async Task The_material_cost_of_a_raft_is_its_material_usages_and_neither_purchase_lines_nor_seeding_touch_it_or_the_debt()
    {
        var world = await factory.WorldAsync();
        var raftId = (await world.Owner.CreateRaftAsync(world.Zone.Id)).Id();
        var seed = await world.CompletedAsync(new LineBody(world.Seed, 10m, 1_000m, RaftId: raftId));
        await world.CompletedAsync(new LineBody(world.Material, 3m, 2_000m, RaftId: raftId));
        var debt = await world.DebtAsync();
        var cycle = await world.Owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-1)));

        await world.Owner.RecordSeedingAsync(cycle.Id(), new SeedingBody(Today, seed.LineOf(world.Seed), [1], 5m));

        Assert.Equal(0m, await world.Owner.RaftMaterialCostAsync(raftId));
        await factory.UseMaterialAsync(raftId, world.Material, 2);
        Assert.Equal(4_000m, await world.Owner.RaftMaterialCostAsync(raftId));
        Assert.Equal(debt, await world.DebtAsync());
    }

    // BR-PURCHASE-10 across the merge: an item that is only on a material usage record is used too (conflict in PurchasingRepositories).
    [Fact]
    public async Task An_item_on_a_material_usage_only_keeps_its_group_and_unit()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var itemId = await owner.CreateItemAsync("Material", "piece");
        await owner.RecordUsageAsync(setup.RaftId, itemId);
        var item = await owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{itemId}");
        Assert.True(item.Flag("isUsed"));

        var change = await owner.PutAsJsonAsync($"/api/farming-input-items/{itemId}", new
        {
            code = item.Text("code"), name = item.Text("name"), group = "Material", unit = "box", rowVersion = item.Text("rowVersion"),
        });

        Assert.Equal("Unit", await change.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }
}
