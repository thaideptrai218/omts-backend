using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>Regressions of the bugs QA found in the OMTS-210 review fixes.</summary>
public class ReviewFixQaRegressionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    /// <summary>
    /// Expected: 1 and 1.0 are the same JSON number, so a re-save that writes 1.0 for 1 is not a drawing change.
    /// Actual: JsonNode.DeepEquals compares the raw number text, so the re-save records a Raft.LayoutDrawingChanged row
    /// (and bumps the row version on save).
    /// </summary>
    [Fact]
    public async Task Number_1_and_1_0_in_a_drawing_is_not_a_drawing_change()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var code = NewCode();
        var created = await (await owner.PostAsync($"/api/zones/{zone.Id()}/rafts", ReviewFixQaTests.RawRaft(NewRaftBody(code), "{\"scale\":1}")))
            .ReadAsync(HttpStatusCode.Created);

        await (await owner.PutAsync($"/api/rafts/{created.Id()}", ReviewFixQaTests.RawRaft(NewRaftBody(code) with { RowVersion = created.Text("rowVersion") }, "{\"scale\":1.0}")))
            .ReadAsync(HttpStatusCode.OK);

        var drawingChanges = await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == created.Id() && e.Action == "Raft.LayoutDrawingChanged"));
        Assert.True(drawingChanges == 0, $"Expected 0 Raft.LayoutDrawingChanged rows for 1 -> 1.0, actual {drawingChanges}.");
    }
}
