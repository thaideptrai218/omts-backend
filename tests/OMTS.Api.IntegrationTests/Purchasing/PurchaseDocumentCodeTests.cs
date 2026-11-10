using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// Codes typed by the user on purchase receipts, purchase returns and supplier payments: kept as typed, unique per kind regardless
/// of letter case, generated when left empty (skipping a number someone typed), and fixed once the document counts.
/// </summary>
public class PurchaseDocumentCodeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private static string CodeOf(JsonElement document) => document.GetProperty("code").GetString()!;

    [Fact]
    public async Task A_typed_receipt_code_is_kept_as_typed_names_the_seed_lots_and_is_refused_again_in_any_case()
    {
        var world = await factory.WorldAsync();
        var typed = Unique("Kv.a_");
        var receipt = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 10, 900)) with { Complete = true, Code = $"  {typed} " });

        Assert.Equal(typed, CodeOf(receipt));
        Assert.Equal($"{typed}-1", receipt.SeedLotCodeOf(world.Seed));

        var duplicate = await (await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 1)) with { Code = typed.ToUpperInvariant() }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal(("Code", receipt.Id()), (duplicate.GetProperty("field").GetString(), duplicate.GetProperty("existingId").GetGuid()));

        // Empty means generated; a space or a 31st character is not a code.
        Assert.Matches(@"^PN\d{6,}$", CodeOf(await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1)) with { Code = "  " })));
        Assert.Equal("Code", await (await world.Owner.PostReceiptAsync(world.Body() with { Code = "PN 01" })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Code", await (await world.Owner.PostReceiptAsync(world.Body() with { Code = new string('A', 31) })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task The_generator_skips_a_number_whose_code_was_typed()
    {
        var world = await factory.WorldAsync();
        var first = await world.Owner.CreateReceiptAsync(world.Body());
        var number = long.Parse(CodeOf(first)[2..], CultureInfo.InvariantCulture);
        var taken = $"pn{number + 1:000000}";
        Assert.Equal(taken, CodeOf(await world.Owner.CreateReceiptAsync(world.Body() with { Code = taken })));

        var next = await world.Owner.CreateReceiptAsync(world.Body());

        Assert.Equal($"PN{number + 2:000000}", CodeOf(next));
    }

    [Fact]
    public async Task A_draft_code_may_change_and_a_completed_receipt_keeps_its_code()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 2, 100)));
        var renamed = Unique("DRAFT");
        var completed = await (await world.Owner.EditAsync(draft, world.Body(new LineBody(world.Seed, 2, 100)) with { Code = renamed, Complete = true }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Completed", renamed), (completed.GetProperty("status").GetString(), CodeOf(completed)));

        var line = new LineBody(world.Seed, 2, 100, LineId: completed.LineOf(world.Seed));
        Assert.Equal("Code", await (await world.Owner.EditAsync(completed, world.Body(line) with { Code = Unique("OTHER") }))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var same = await (await world.Owner.EditAsync(completed, world.Body(line with { Quantity = 3 }) with { Code = renamed }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal((renamed, $"{renamed}-1"), (CodeOf(same), same.SeedLotCodeOf(world.Seed)));
    }

    [Fact]
    public async Task Typed_return_and_payment_codes_are_unique_per_kind_and_a_recorded_payment_keeps_its_code()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 1_000));
        var code = Unique("TH");

        var returned = await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(receipt.LineOf(world.Other), 1)], Code = code }))
            .ReadAsync(HttpStatusCode.Created);
        Assert.Contains(returned.GetProperty("returns").EnumerateArray(), r => CodeOf(r) == code);
        var again = await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(receipt.LineOf(world.Other), 1)], Code = code.ToLowerInvariant() });
        Assert.Equal("Code", await again.FieldAsync(HttpStatusCode.Conflict, "MSG-GEN-02"));

        // Each kind has codes of its own: the return's code is free for a payment, once.
        var payment = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1_000, Code = code }))
            .ReadAsync(HttpStatusCode.Created);
        Assert.Equal(code, CodeOf(payment));
        Assert.Equal("Code", await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1_000, Code = code }))
            .FieldAsync(HttpStatusCode.Conflict, "MSG-GEN-02"));

        var rowVersion = payment.GetProperty("rowVersion").GetString();
        Assert.Equal("Code", await (await world.Owner.PutAsJsonAsync($"/api/supplier-payments/{payment.Id()}", new { rowVersion, method = "Cash", code = Unique("TT") }))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await (await world.Owner.PutAsJsonAsync($"/api/supplier-payments/{payment.Id()}", new { rowVersion, method = "Cash", note = "kept", code }))
            .ReadAsync(HttpStatusCode.OK);
    }
}
