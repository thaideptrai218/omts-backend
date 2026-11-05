using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Repositories;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>A theory that needs a real SQL Server, as <see cref="SqlServerFactAttribute"/>.</summary>
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerFactAttribute.VariableName)))
        {
            Skip = $"Set {SqlServerFactAttribute.VariableName} to a SQL Server connection string to run database tests.";
        }
    }
}

/// <summary>A clock a test moves: the instant it answers until the test sets another.</summary>
internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>What a race test runs at one exact moment of a request, on its own connection, as a parallel session would. Each is taken once.</summary>
internal sealed class RaceMoments
{
    public Func<Task>? BeforeSave { get; set; }

    public Func<Task>? BeforeVersionsRead { get; set; }

    public Func<Task>? TakeBeforeSave()
    {
        var moment = BeforeSave;
        BeforeSave = null;
        return moment;
    }

    public Func<Task>? TakeBeforeVersionsRead()
    {
        var moment = BeforeVersionsRead;
        BeforeVersionsRead = null;
        return moment;
    }
}

/// <summary>The unit of work of the host, with <see cref="RaceMoments.BeforeSave"/> first.</summary>
internal sealed class MomentUnitOfWork(IUnitOfWork inner, RaceMoments moments) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (moments.TakeBeforeSave() is { } moment)
        {
            await moment();
        }

        return await inner.SaveChangesAsync(cancellationToken);
    }

    public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

    public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

    public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);
}

/// <summary>The history of the attendance settings, with <see cref="RaceMoments.BeforeVersionsRead"/> first.</summary>
internal sealed class MomentAttendanceSettings(IAttendanceSettingsRepository inner, RaceMoments moments) : IAttendanceSettingsRepository
{
    public async Task<IReadOnlyList<AttendanceSettingsVersion>> ListAsync(CancellationToken cancellationToken)
    {
        if (moments.TakeBeforeVersionsRead() is { } moment)
        {
            await moment();
        }

        return await inner.ListAsync(cancellationToken);
    }

    public void Add(AttendanceSettingsVersion version) => inner.Add(version);
}

/// <summary>Set-up of the QA tests of PR L2 (OMTS-227). Payrolls and rates date from fixed days; nothing is computed at discovery.</summary>
internal static class LaborSettingsQaKit
{
    public static Task<HttpResponseMessage> PostRawAsync(this HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    public static Task<HttpResponseMessage> PutRawAsync(this HttpClient client, string path, string json) =>
        client.PutAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>Every property name in the document, at any depth.</summary>
    public static IEnumerable<string> PropertyNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };

    /// <summary>A 500 fails the test with the body and what the host logged, so a deadlock shows its message.</summary>
    public static async Task AssertNo500Async(IEnumerable<HttpResponseMessage> responses, CapturingLoggerProvider? logs = null)
    {
        foreach (var response in responses)
        {
            await response.Content.LoadIntoBufferAsync();
            var text = await response.Content.ReadAsStringAsync();
            var logged = logs is null
                ? string.Empty
                : string.Join(" | ", logs.Lines.Where(l => l.Contains("Unhandled exception") || l.Contains("deadlock", StringComparison.OrdinalIgnoreCase)).Take(3));
            Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {text} {logged}");
        }
    }

    /// <summary>The API on the same database, whose unit of work and settings history stop at the moments of the test.</summary>
    public static WebApplicationFactory<Program> WithMoments(this SqlServerApiFactory api, RaceMoments moments) =>
        api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new MomentUnitOfWork(new UnitOfWork(provider.GetRequiredService<OmtsDbContext>()), moments));
            services.RemoveAll<IAttendanceSettingsRepository>();
            services.AddScoped<IAttendanceSettingsRepository>(provider => new MomentAttendanceSettings(new AttendanceSettingsRepository(provider.GetRequiredService<OmtsDbContext>()), moments));
        }));

    /// <summary>A logged-in account on any host of the database: an Owner unless a role is given, as a staff account needs a pair to log in; the password is new for each call.</summary>
    public static async Task<(HttpClient Client, Account Account)> LoginNewAsync(WebApplicationFactory<Program> app, StaffRole? role = null, Guid? zoneId = null)
    {
        var password = NewPassword();
        var code = NewCode();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var account = new Account
        {
            Username = code,
            DisplayName = code,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(password),
            SubjectType = role is null ? SubjectType.CooperativeOwner : SubjectType.Staff,
            PhoneNumber = NewPhone(),
            PhoneVerifiedAt = DateTimeOffset.UtcNow,
            StatusId = StatusIds.Account.Active,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        if (role is { } staffRole)
        {
            await app.GrantAsync(account, staffRole, zoneId);
        }

        var client = app.CreateClient();
        var tokens = await LoginTokensAsync(client, account.Username, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, account);
    }

    /// <summary>A payroll written on its own connection, as a parallel session would.</summary>
    public static async Task AddPayrollElsewhereAsync(string connectionString, string start, string end, int statusId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("INSERT INTO Payrolls (Id, PeriodStart, PeriodEnd, StatusId, CreatedBySystem, CreatedAt) VALUES (NEWID(), @s, @e, @status, 0, SYSDATETIMEOFFSET())", connection);
        command.Parameters.AddWithValue("@s", DateOnly.Parse(start).ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@e", DateOnly.Parse(end).ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@status", statusId);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>A migrated database with a cooperative and an Owner, shared by the tests of one class: names and dates carry a unique tag.</summary>
public sealed class LaborSqlFixture : IAsyncLifetime
{
    public SqlServerTestDatabase Database { get; private set; } = null!;

    public SqlServerApiFactory Api { get; private set; } = null!;

    public HttpClient Owner { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerFactAttribute.VariableName)))
        {
            return;
        }

        Database = SqlServerTestDatabase.Create();
        Api = await FarmLabSqlServerKit.StartAsync(Database);
        (Owner, _, _) = await FarmLabSqlServerKit.OwnerWithCooperativeAsync(Api);
    }

    public async Task DisposeAsync()
    {
        if (Database is null)
        {
            return;
        }

        await Api.DisposeAsync();
        await Database.DisposeAsync();
    }
}
