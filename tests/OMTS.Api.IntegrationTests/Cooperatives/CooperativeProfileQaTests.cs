using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Platform;
using OMTS.Infrastructure.Bootstrap;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeCertificationRequests;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileQaKit;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// QA of OMTS-217 PR 0.7, FARM-01 profile: the English introduction fallback of the provenance block (BR-ORIGIN-02)
/// and the cooperative without a status (SRS 27/10) from the deployment bootstrap on.
/// </summary>
public class CooperativeProfileQaTests
{
    // A blank English introduction is stored empty, so the block falls back to the Vietnamese one; there is no fallback
    // the other way round (the page opens in Vietnamese). 4000 characters are accepted in both languages.
    [Fact]
    public async Task Blank_english_introduction_falls_back_to_the_vietnamese_one_but_never_the_reverse()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        var vietnamese = new string('v', 4000);

        var blank = (await (await owner.PutAsJsonAsync(Profile, Valid(await RowVersionAsync(owner), b =>
        {
            b["introductionVi"] = vietnamese;
            b["introductionEn"] = " \t ";
        }))).ReadAsync(HttpStatusCode.OK)).GetProperty("profile");
        var englishOnly = (await (await owner.PutAsJsonAsync(Profile, Valid(blank.Text("rowVersion"), b =>
        {
            b["introductionVi"] = "  ";
            b["introductionEn"] = new string('e', 4000);
        }))).ReadAsync(HttpStatusCode.OK)).GetProperty("profile");

        Assert.Equal(JsonValueKind.Null, blank.GetProperty("introductionEn").ValueKind);
        Assert.Equal((vietnamese, vietnamese), (blank.GetProperty("publicPreview").Text("introductionVi"), blank.GetProperty("publicPreview").Text("introductionEn")));
        Assert.Equal(JsonValueKind.Null, englishOnly.GetProperty("publicPreview").GetProperty("introductionVi").ValueKind);
        Assert.Equal(new string('e', 4000), englishOnly.GetProperty("publicPreview").Text("introductionEn"));
        var stored = await api.QueryAsync(db => db.Cooperatives.AsNoTracking().SingleAsync());
        Assert.Equal((null, new string('e', 4000)), (stored.IntroductionVi, stored.IntroductionEn));
    }

    // SRS 27/10: the cooperative has no status. A fresh bootstrap creates it without one, the catalog has no
    // COOPERATIVE group, readiness is green, and the Owner's profile answer carries no status.
    [Fact]
    public async Task Fresh_bootstrap_creates_a_cooperative_without_status_and_is_ready()
    {
        await using var api = BootstrapTests.WithSecrets(new AuthApiFactory());

        var outcome = await RunBootstrapAsync(api);

        Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.StartsWith("Cooperative created with Cooperative Owner 'owner'.", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Messages, m => m.Contains("Preliminary", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/health/ready")).StatusCode);
        Assert.False(await QueryAsync(api, db => db.Statuses.AnyAsync(s => s.Group == "COOPERATIVE" || s.Id == 201 || s.Id == 202)));
        Assert.False(await QueryAsync(api, db => db.StatusTransitions.AnyAsync(t => t.Id == 3)));
    }

    // GBR-02 / SRS 27/10: the audit row of the cooperative created at deployment records no status, since the
    // cooperative has none (QA 0.7 #2).
    [Fact]
    public async Task Bootstrap_audit_of_the_new_cooperative_records_no_status()
    {
        await using var api = BootstrapTests.WithSecrets(new AuthApiFactory());

        await RunBootstrapAsync(api);

        var created = await QueryAsync(api, db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "Cooperative.Created"));
        Assert.DoesNotContain("Status", JsonDocument.Parse(created.After!).RootElement.EnumerateObject().Select(p => p.Name));
    }

    private static async Task<BootstrapOutcome> RunBootstrapAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(false, CancellationToken.None);
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> api, Func<OMTS.Infrastructure.Persistence.OmtsDbContext, Task<T>> query)
    {
        using var scope = api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OMTS.Infrastructure.Persistence.OmtsDbContext>());
    }
}
