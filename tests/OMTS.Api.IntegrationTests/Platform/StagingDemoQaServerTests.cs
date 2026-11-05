using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// README "Demo on a shared server", followed to the letter by the real process: one seed, then the real API in
/// Staging, in Production and, as a control, in Development, all on the same temporary database.
/// </summary>
public sealed class StagingDemoQaServerFixture : IAsyncLifetime
{
    public StagingDemoQaProcessKit? Kit { get; private set; }
    public string Connection { get; private set; } = string.Empty;
    public string Secret { get; } = AcsTestKit.NewPassword();
    public string SeedOutput { get; private set; } = string.Empty;
    public int SeedExitCode { get; private set; }
    public string OwnerTemporaryPassword { get; private set; } = string.Empty;
    public HttpStatusCode FirstCodeRequestStatus { get; private set; }
    public string FirstCodeRequestBody { get; private set; } = string.Empty;
    public StagingDemoQaProcessKit.RunningApi? Staging { get; private set; }
    public StagingDemoQaProcessKit.RunningApi? Production { get; private set; }
    public StagingDemoQaProcessKit.RunningApi? Development { get; private set; }

    public Dictionary<string, string?> Settings(string environment)
    {
        var settings = Kit!.ServerSettings(Connection);
        settings["ASPNETCORE_ENVIRONMENT"] = environment;
        settings["Demo__AllowOutsideDevelopment"] = "true";
        settings["Demo__Password"] = Secret;
        return settings;
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerFactAttribute.VariableName)))
        {
            return;
        }

        Kit = new StagingDemoQaProcessKit();
        Connection = StagingDemoQaProcessKit.NewDatabaseConnectionString();
        var seedSettings = Settings("Staging");
        OwnerTemporaryPassword = seedSettings[StagingDemoQaProcessKit.EnvKey("Bootstrap:Owner:TemporaryPassword")]!;
        var seed = await Kit.RunAsync(seedSettings, "seed", "--bootstrap", "--demo");
        SeedOutput = seed.Output;
        SeedExitCode = seed.ExitCode;

        // The same signing keys for all three, as one server would have, and the file storage of the seed.
        var keys = new Dictionary<string, string?>
        {
            ["Jwt__SigningKey"] = seedSettings["Jwt__SigningKey"],
            ["Storage__Local__SigningKey"] = seedSettings["Storage__Local__SigningKey"],
            ["Jobs__OrphanFileCleanup__Enabled"] = "false",
            ["RateLimits__LoginsPerMinute"] = "100000",
            ["RateLimits__CodeRequestsPerMinute"] = "100000",
        };

        Dictionary<string, string?> ApiSettings(string environment)
        {
            var settings = Settings(environment);
            foreach (var (key, value) in keys)
            {
                settings[key] = value;
            }

            return settings;
        }

        Staging = await Kit.StartApiAsync(ApiSettings("Staging"));

        // First, as a request limit applies per account: the result the Sms None row of the change list promises.
        var login = await Staging.Client.PostAsJsonAsync("/api/auth/login", new { identifier = "nhanvien.chuaxacthuc", password = Secret });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        using var codeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/me/phone-verification") { Content = JsonContent.Create(new { }) };
        codeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var code = await Staging.Client.SendAsync(codeRequest);
        FirstCodeRequestStatus = code.StatusCode;
        FirstCodeRequestBody = await code.Content.ReadAsStringAsync();

        Production = await Kit.StartApiAsync(ApiSettings("Production"));
        Development = await Kit.StartApiAsync(ApiSettings("Development"));
    }

    public async Task DisposeAsync()
    {
        Staging?.Dispose();
        Production?.Dispose();
        Development?.Dispose();
        if (Kit is not null)
        {
            await StagingDemoQaProcessKit.DropDatabaseAsync(Connection);
            Kit.Dispose();
        }
    }
}

public partial class StagingDemoQaServerTests(StagingDemoQaServerFixture server) : IClassFixture<StagingDemoQaServerFixture>
{
    private static async Task<(HttpStatusCode Status, JsonElement Body)> LoginAsync(HttpClient client, string identifier, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier, password });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    private OmtsDbContext Database() => new(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(server.Connection).Options);

    [SqlServerFact]
    public void The_readme_recipe_seeds_and_the_api_is_ready()
    {
        Assert.Equal(0, server.SeedExitCode);
        Assert.False(server.Staging!.HasExited, server.Staging.Output);
        Assert.Contains("Demo trader accounts: 2 created.", server.SeedOutput);
    }

    [SqlServerFact]
    public async Task Readiness_answers_green_and_gives_nothing_but_status_on_staging()
    {
        var ready = await server.Staging!.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.DoesNotContain(server.Secret, body);
        Assert.DoesNotContain("Staging", body);
    }

    [SqlServerFact]
    public async Task All_nine_logins_use_the_secret_and_the_well_known_password_answers_401_on_the_real_server()
    {
        var client = server.Staging!.Client;
        foreach (var username in new[] { "owner", "quanlyvung1", "quanlyvung2", "ketoan", "thuonglai1", "nhanvien.doimk", "nhanvien.chuaxacthuc" })
        {
            Assert.True((await LoginAsync(client, username, server.Secret)).Status == HttpStatusCode.OK, username);
            Assert.True((await LoginAsync(client, username, DemoSeedGate.WellKnownPassword)).Status == HttpStatusCode.Unauthorized, username);
        }

        foreach (var username in new[] { "thuonglai2", "nhanvien.hethan" })
        {
            var (status, body) = await LoginAsync(client, username, server.Secret);
            Assert.True(status == HttpStatusCode.Forbidden, username);
            Assert.Equal("MSG-ACCOUNT-07", body.GetProperty("code").GetString());
        }

        // The first password the bootstrap issued to the Owner is gone as well.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "owner", server.OwnerTemporaryPassword)).Status);
    }

    // Reads every text column of the database the seed filled, the console of the seed and of the API, and the files.
    [SqlServerFact]
    public async Task The_secret_is_nowhere_but_hashed_and_no_audit_row_holds_a_hash()
    {
        Assert.DoesNotContain(server.Secret, server.SeedOutput);
        Assert.DoesNotContain(server.Secret, server.Staging!.Output);
        Assert.DoesNotContain(server.Secret, server.Production!.Output);
        Assert.DoesNotContain(server.Secret, server.Development!.Output);
        foreach (var file in Directory.EnumerateFiles(server.Kit!.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(server.Secret, await File.ReadAllTextAsync(file));
        }

        await using var connection = new SqlConnection(server.Connection);
        await connection.OpenAsync();
        var columns = new List<(string Table, string Column)>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE DATA_TYPE IN ('nvarchar','varchar','nchar','char','ntext','text')";
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        await using var db = Database();
        var hashes = await db.Accounts.Select(a => a.PasswordHash).Distinct().ToListAsync();
        var found = new List<string>();
        foreach (var (table, column) in columns)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM [{table}] WHERE CHARINDEX(@secret, [{column}]) > 0";
            count.Parameters.AddWithValue("@secret", server.Secret);
            if ((int)(await count.ExecuteScalarAsync())! > 0)
            {
                found.Add($"secret in {table}.{column}");
            }

            if (table == "Accounts" && column == "PasswordHash")
            {
                continue;
            }

            foreach (var hash in hashes)
            {
                await using var hashCount = connection.CreateCommand();
                hashCount.CommandText = $"SELECT COUNT(*) FROM [{table}] WHERE CHARINDEX(@hash, [{column}]) > 0";
                hashCount.Parameters.AddWithValue("@hash", hash);
                if ((int)(await hashCount.ExecuteScalarAsync())! > 0)
                {
                    found.Add($"hash in {table}.{column}");
                }
            }
        }

        Assert.Empty(found);
        var audit = await db.AuditEvents.Where(e => e.Action == "Account.DemoCredentialsSet").ToListAsync();
        Assert.Equal(7, audit.Count);
        Assert.All(audit, row => Assert.DoesNotContain("Password\":\"", $"{row.Before}{row.After}"));
    }

    // Real process, no test-host settings: the only way to show appsettings.Development.json does not load here.
    [SqlServerFact]
    public void The_real_staging_host_does_not_read_the_development_file_and_warns_about_sms_push_and_ocr()
    {
        var log = server.Staging!.Output;

        Assert.Contains("SMS provider is None", log);
        Assert.Contains("Push provider is None", log);
        Assert.Contains("OCR provider '", log);
        Assert.DoesNotContain("Email provider is Fake", log);
        Assert.DoesNotContain("SMS provider is Fake", log);
    }
}
