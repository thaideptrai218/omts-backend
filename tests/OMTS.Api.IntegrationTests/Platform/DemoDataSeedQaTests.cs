using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.Services;
using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Common;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA-1 checks of the Development demo seed (OMTS-213): later reruns, partial seeds, changed passwords, the ambient actor.</summary>
public class DemoDataSeedQaTests
{
    private const string DemoPassword = "Demo@1234";
    private const string OwnerTemporaryPassword = "Temp2026ab";

    // Every table the demo seed writes to (same list as DemoDataSeedTests).
    private static readonly (string Table, Func<OmtsDbContext, Task<int>> Count)[] SeededTables =
    [
        ("Cooperatives", db => db.Cooperatives.CountAsync()),
        ("Accounts", db => db.Accounts.CountAsync()),
        ("AccessGrants", db => db.AccessGrants.CountAsync()),
        ("Customers", db => db.Customers.CountAsync()),
        ("Employees", db => db.Employees.CountAsync()),
        ("FarmingZones", db => db.FarmingZones.CountAsync()),
        ("ZoneComplianceDocuments", db => db.ZoneComplianceDocuments.CountAsync()),
        ("Rafts", db => db.Rafts.CountAsync()),
        ("Lanes", db => db.Lanes.CountAsync()),
        ("Suppliers", db => db.Suppliers.CountAsync()),
        ("FarmingInputItems", db => db.FarmingInputItems.CountAsync()),
        ("PurchaseReceipts", db => db.PurchaseReceipts.CountAsync()),
        ("PurchaseReceiptLines", db => db.PurchaseReceiptLines.CountAsync()),
        ("SeedReceiptLots", db => db.SeedReceiptLots.CountAsync()),
        ("SupplierPayments", db => db.SupplierPayments.CountAsync()),
        ("PurchaseReturns", db => db.PurchaseReturns.CountAsync()),
        ("AuditEvents", db => db.AuditEvents.CountAsync()),
    ];

    private static readonly MethodInfo CountUpdatedMethod =
        typeof(DemoDataSeedQaTests).GetMethod(nameof(CountUpdatedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    // Demo dates are relative to "today" of the run that created them; a later run must neither add rows nor move a date.
    [SqlServerFact]
    public async Task Demo_rerun_on_a_later_day_adds_no_rows_moves_no_date_and_updates_nothing()
    {
        var clock = new ShiftedClock();
        await using var host = new DemoHost(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

        var start = clock.GetUtcNow();
        var first = await RunAsync(host.App, demo: true);
        var counts = await CountsAsync(host.App);
        var dates = await DemoDatesAsync(host.App);
        Assert.Equal(DeploymentBootstrapper.Success, first.ExitCode);
        Assert.Same(clock, host.App.Services.GetRequiredService<TimeProvider>());
        Assert.Contains(dates, d => d.StartsWith("license "));
        Assert.Contains(dates, d => d.StartsWith("receipt "));
        // The probe used below does see updates: the first run discontinued zones and rafts, completed receipts…
        Assert.NotEmpty(await UpdatedSinceAsync(host.App, start));

        // Tomorrow, then six weeks later: the "expiring" license has expired by then and every receipt is older.
        foreach (var days in new[] { 1, 45 })
        {
            clock.Shift = TimeSpan.FromDays(days);
            var since = clock.GetUtcNow();

            var later = await RunAsync(host.App, demo: true);

            Assert.Equal(DeploymentBootstrapper.Success, later.ExitCode);
            Assert.Contains(later.Messages, m => m.StartsWith("Demo "));
            Assert.All(later.Messages.Where(m => m.StartsWith("Demo ")), m => Assert.EndsWith("already there.", m));
            Assert.Equal(counts, await CountsAsync(host.App));
            Assert.Equal(dates, await DemoDatesAsync(host.App));
            // Every save stamps UpdatedAt from the moved clock, so any row the rerun touched would show here.
            Assert.Empty(await UpdatedSinceAsync(host.App, since));
        }
    }

    // A deployment bootstrapped without --demo (cooperative and Owner only) can still get the demo data later.
    [SqlServerFact]
    public async Task Demo_run_on_a_database_bootstrapped_without_demo_completes_it_and_a_further_run_creates_nothing()
    {
        await using var host = new DemoHost();

        var plain = await RunAsync(host.App, demo: false);
        var plainCounts = await CountsAsync(host.App);
        var demo = await RunAsync(host.App, demo: true);
        var demoCounts = await CountsAsync(host.App);
        var again = await RunAsync(host.App, demo: true);

        Assert.Equal(DeploymentBootstrapper.Success, plain.ExitCode);
        Assert.Equal(1, plainCounts["Cooperatives"]);
        Assert.Equal(0, plainCounts["FarmingZones"]);
        Assert.Equal(0, plainCounts["Employees"]);
        Assert.Equal(0, plainCounts["PurchaseReceipts"]);

        Assert.Equal(DeploymentBootstrapper.Success, demo.ExitCode);
        Assert.Contains(demo.Messages, m => m.Contains("already initialized"));
        // Every step had work to do, the Owner's login included.
        Assert.Equal(10, demo.Messages.Count(m => m.StartsWith("Demo ")));
        Assert.All(demo.Messages.Where(m => m.StartsWith("Demo ")), m => Assert.EndsWith("created.", m));
        Assert.Equal(1, demoCounts["Cooperatives"]);
        Assert.Equal(3, demoCounts["FarmingZones"]);
        Assert.Equal(20, demoCounts["Rafts"]);
        Assert.Equal(8, demoCounts["Employees"]);
        Assert.Equal(20, demoCounts["PurchaseReceipts"]);

        Assert.Equal(DeploymentBootstrapper.Success, again.ExitCode);
        Assert.All(again.Messages.Where(m => m.StartsWith("Demo ")), m => Assert.EndsWith("already there.", m));
        Assert.Equal(demoCounts, await CountsAsync(host.App));

        // The Owner never used the temporary password, so the demo password replaced it.
        var client = host.App.CreateClient();
        var demoLogin = await LoginAsync(client, "owner", DemoPassword);
        Assert.Equal(HttpStatusCode.OK, demoLogin.Status);
        Assert.False(demoLogin.Body.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "owner", OwnerTemporaryPassword)).Status);
    }

    // ACS-04 through the API, then a rerun: the Owner, and the staff account that had to change its password at first login.
    [SqlServerFact]
    public async Task Demo_rerun_leaves_alone_an_owner_and_a_staff_member_who_changed_their_password()
    {
        await using var host = new DemoHost();
        await RunAsync(host.App, demo: true);
        string[] usernames = ["owner", "nhanvien.doimk"];
        foreach (var username in usernames)
        {
            await ChangePasswordAsync(host.App, username, $"{username}-New2026");
        }

        var before = await AccountStatesAsync(host.App, usernames);
        var rerun = await RunAsync(host.App, demo: true);
        var after = await AccountStatesAsync(host.App, usernames);

        Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
        Assert.Contains("Demo owner login: already there.", rerun.Messages);
        Assert.Equal(before, after);
        Assert.All(after, a => Assert.False(a.MustChangePassword, a.Username));

        var client = host.App.CreateClient();
        foreach (var username in usernames)
        {
            var login = await LoginAsync(client, username, $"{username}-New2026");
            Assert.Equal(HttpStatusCode.OK, login.Status);
            Assert.False(login.Body.GetProperty("mustChangePassword").GetBoolean(), username);
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, username, DemoPassword)).Status);
        }
    }

    // An Owner's reset of a demo staff password (ACS-09), and a phone verified since, are real use: a rerun keeps
    // both instead of putting the demo login back (the reset sets MustChangePassword again, as a first run would).
    [SqlServerFact]
    public async Task Demo_rerun_keeps_an_owner_reset_of_a_staff_password_and_a_phone_verified_since()
    {
        await using var host = new DemoHost();
        await RunAsync(host.App, demo: true);
        string[] usernames = ["nhanvien.chuaxacthuc", "quanlyvung1"];
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            (await db.Accounts.SingleAsync(a => a.Username == "nhanvien.chuaxacthuc")).PhoneVerifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var client = host.App.CreateClient();
        var owner = host.App.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await LoginAsync(client, "owner", DemoPassword)).Body.GetProperty("accessToken").GetString());
        var temporary = new Dictionary<string, string>();
        foreach (var username in usernames)
        {
            using var scope = host.App.Services.CreateScope();
            var id = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.Where(a => a.Username == username).Select(a => a.Id).SingleAsync();
            var reset = await owner.PostAsync($"/api/accounts/{id}/reset-password", null);
            Assert.True(reset.StatusCode == HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());
            temporary[username] = (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("temporaryPassword").GetString()!;
        }

        var before = await AccountStatesAsync(host.App, usernames);
        var rerun = await RunAsync(host.App, demo: true);
        var after = await AccountStatesAsync(host.App, usernames);

        Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
        Assert.Equal(before, after);
        Assert.All(after, a => Assert.NotNull(a.PhoneVerifiedAt));
        foreach (var username in usernames)
        {
            var login = await LoginAsync(client, username, temporary[username]);
            Assert.Equal(HttpStatusCode.OK, login.Status);
            Assert.True(login.Body.GetProperty("mustChangePassword").GetBoolean(), username);
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, username, DemoPassword)).Status);
        }
    }

    // The seed's ambient Owner must never become the caller of an HTTP request: only the access token counts.
    [Fact]
    public void Current_user_reads_the_ambient_actor_only_outside_an_http_request()
    {
        var actor = new AmbientActor();
        var accessor = new HttpContextAccessor();
        var currentUser = new CurrentUser(accessor, actor);
        var ownerId = Guid.NewGuid();
        var tokenSubject = Guid.NewGuid();

        using (actor.Use(ownerId))
        {
            Assert.Equal(ownerId, currentUser.AccountId);

            accessor.HttpContext = new DefaultHttpContext();
            Assert.Null(currentUser.AccountId);

            accessor.HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(OmtsClaimTypes.Subject, tokenSubject.ToString())], "Bearer")),
            };
            Assert.Equal(tokenSubject, currentUser.AccountId);

            accessor.HttpContext = null;
        }

        // Disposing the handle ends the acting.
        Assert.Null(currentUser.AccountId);
    }

    // Even with the Owner set as the ambient actor in every request scope, requests are who their token says.
    [Fact]
    public async Task Requests_ignore_an_ambient_owner_in_their_scope()
    {
        await using var factory = new AmbientOwnerApiFactory();
        await factory.EnsureCooperativeAsync();
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        factory.ActAs = owner.Id;
        var anonymous = factory.CreateClient();
        var ownerClient = await factory.LoginAsync(owner.Username);
        var traderClient = await factory.LoginAsync(trader.Username);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/cooperative/profile")).StatusCode);

        var me = await traderClient.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(trader.Id, me.GetProperty("accountId").GetGuid());
        var ownerOnly = await traderClient.GetAsync("/api/cooperative/profile");
        Assert.Equal(HttpStatusCode.Forbidden, ownerOnly.StatusCode);
        Assert.Equal("MSG02", await ownerOnly.ErrorCodeAsync());

        // The same endpoint is open to the real Owner, so the refusals above are about the caller.
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync("/api/cooperative/profile")).StatusCode);
        Assert.True(factory.ActedRequests >= 5, $"ambient actor set on {factory.ActedRequests} requests");
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> app, bool demo)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(demo, CancellationToken.None);
    }

    private static async Task<IReadOnlyDictionary<string, int>> CountsAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var counts = new Dictionary<string, int>();
        foreach (var (table, count) in SeededTables)
        {
            counts[table] = await count(db);
        }

        return counts;
    }

    /// <summary>Every date the demo derives from "today", one line per row, in a stable order.</summary>
    private static async Task<List<string>> DemoDatesAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var licenses = await db.ZoneComplianceDocuments.Select(d => new { d.DocumentNumber, d.EffectiveDate, d.ExpiryDate }).ToListAsync();
        var receipts = await db.PurchaseReceipts.Select(r => new { r.Id, r.ReceiptDate, r.StatusId }).ToListAsync();
        var payments = await db.SupplierPayments.Select(p => new { p.Id, p.PaymentDate, p.StatusId }).ToListAsync();
        var returns = await db.PurchaseReturns.Select(r => new { r.Id, r.ReturnDate }).ToListAsync();
        var grants = await db.AccessGrants.Select(g => new { g.Id, g.EffectiveFrom, g.EffectiveTo }).ToListAsync();
        var employees = await db.Employees.Select(e => new { e.Id, e.StatusId, e.EmploymentEndDate }).ToListAsync();

        return licenses.Select(x => $"license {x}")
            .Concat(receipts.Select(x => $"receipt {x}"))
            .Concat(payments.Select(x => $"payment {x}"))
            .Concat(returns.Select(x => $"return {x}"))
            .Concat(grants.Select(x => $"grant {x}"))
            .Concat(employees.Select(x => $"employee {x}"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>"Table: n" for each auditable table with rows saved at or after <paramref name="since"/>.</summary>
    private static async Task<List<string>> UpdatedSinceAsync(WebApplicationFactory<Program> app, DateTimeOffset since)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var updated = new List<string>();
        foreach (var type in db.Model.GetEntityTypes().Where(t => t.BaseType is null && !t.IsOwned() && typeof(AuditableEntity).IsAssignableFrom(t.ClrType)))
        {
            var count = await (Task<int>)CountUpdatedMethod.MakeGenericMethod(type.ClrType).Invoke(null, [db, since])!;
            if (count > 0)
            {
                updated.Add($"{type.ClrType.Name}: {count}");
            }
        }

        return updated;
    }

    private static Task<int> CountUpdatedAsync<T>(OmtsDbContext db, DateTimeOffset since) where T : AuditableEntity =>
        db.Set<T>().CountAsync(e => e.UpdatedAt >= since);

    private static async Task<List<AccountState>> AccountStatesAsync(WebApplicationFactory<Program> app, string[] usernames)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var accounts = await db.Accounts.Where(a => usernames.Contains(a.Username)).OrderBy(a => a.Username).ToListAsync();
        return accounts.Select(a => new AccountState(a.Username, a.PasswordHash, a.MustChangePassword, a.TemporaryPasswordIssuedAt, a.PhoneVerifiedAt)).ToList();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier = username, password });
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    /// <summary>Logs in with the demo password and changes it (ACS-04), proving it by the current password.</summary>
    private static async Task ChangePasswordAsync(WebApplicationFactory<Program> app, string username, string newPassword)
    {
        var client = app.CreateClient();
        var (status, tokens) = await LoginAsync(client, username, DemoPassword);
        Assert.Equal(HttpStatusCode.OK, status);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var changed = await client.PostAsJsonAsync("/api/me/password", new { currentPassword = DemoPassword, newPassword, passwordConfirmation = newPassword });

        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
    }

    private sealed record AccountState(
        string Username, string PasswordHash, bool MustChangePassword, DateTimeOffset? TemporaryPasswordIssuedAt, DateTimeOffset? PhoneVerifiedAt);

    /// <summary>The system clock moved forward by <see cref="Shift"/>, for a run "on a later day".</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }

    /// <summary>A fresh SQL Server database with the bootstrap secrets of a demo deployment; dropped with its files.</summary>
    private sealed class DemoHost : IAsyncDisposable
    {
        private readonly SqlServerTestDatabase _database = SqlServerTestDatabase.Create();
        private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        private readonly SqlServerApiFactory _api;

        public DemoHost(Action<IServiceCollection>? configureServices = null)
        {
            _api = new SqlServerApiFactory(_database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã Nuôi hàu Vân Đồn",
                [BootstrapSettings.OwnerDisplayNameKey] = "Nguyễn Văn Hải",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = OwnerTemporaryPassword,
                ["Storage:Local:RootPath"] = _storageRoot,
            });
            App = configureServices is null ? _api : _api.WithWebHostBuilder(b => b.ConfigureTestServices(configureServices));
        }

        public WebApplicationFactory<Program> App { get; }

        public async ValueTask DisposeAsync()
        {
            // Disposing the factory also disposes the host derived from it.
            await _api.DisposeAsync();
            await _database.DisposeAsync();
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
    }

    /// <summary>The API with <see cref="ActAs"/> set as the ambient actor of every request scope, as if a seed leaked into it.</summary>
    private sealed class AmbientOwnerApiFactory : AuthApiFactory
    {
        private int _actedRequests;

        public Guid? ActAs { get; set; }

        /// <summary>Requests that ran with the ambient actor set, to show the middleware really ran.</summary>
        public int ActedRequests => Volatile.Read(ref _actedRequests);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new ActAsStartupFilter(this)));
        }

        private sealed class ActAsStartupFilter(AmbientOwnerApiFactory factory) : IStartupFilter
        {
            // First in the pipeline, before authentication.
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    using var acting = factory.ActAs is { } id ? context.RequestServices.GetRequiredService<AmbientActor>().Use(id) : null;
                    if (acting is not null)
                    {
                        Interlocked.Increment(ref factory._actedRequests);
                    }

                    await nextMiddleware(context);
                });
                next(app);
            };
        }
    }
}
