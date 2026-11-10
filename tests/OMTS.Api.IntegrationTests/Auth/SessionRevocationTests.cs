using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>
/// An account that stops being Active loses every session in the same save, and a lock that races a login leaves no
/// usable session behind. Trader accounts can also be locked outside the application (a direct database change), which
/// revokes nothing by itself.
/// </summary>
public class SessionRevocationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "Secret123";

    [Fact]
    public async Task Locking_a_staff_account_revokes_every_session_with_the_lock_as_reason()
    {
        var owner = await OwnerClientAsync();
        var staff = await StaffAsync();
        var first = factory.CreateClient();
        var second = factory.CreateClient();
        var firstTokens = await LoginTokensAsync(first, staff.Username);
        await LoginTokensAsync(second, staff.Username);

        var locked = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/lock", new { reason = "Left the cooperative" });

        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        var sessions = await factory.QueryAsync(db => db.LoginSessions.AsNoTracking().Where(s => s.AccountId == staff.Id).ToListAsync());
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s =>
        {
            Assert.NotNull(s.RevokedAt);
            Assert.Equal("Account locked", s.RevokeReason);
        });
        var withOldToken = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        withOldToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firstTokens.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.SendAsync(withOldToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = firstTokens.GetProperty("refreshToken").GetString() })).StatusCode);
    }

    // The lock commits after the login checked the status and saved the account, before the session is stored: the lock
    // finds no session to revoke, so the login itself must notice and end the session it just stored.
    [Fact]
    public async Task Lock_that_commits_before_the_session_is_stored_leaves_no_usable_session()
    {
        var owner = await OwnerClientAsync();
        var staff = await StaffAsync();
        var race = new LockRace(() => owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/lock", new { reason = "Racing the login" }));
        await using var racing = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new LockBeforeSessionSaveUnitOfWork(provider.GetRequiredService<OmtsDbContext>(), race));
        }));
        race.Arm();

        var login = await racing.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Password });

        Assert.Equal(HttpStatusCode.OK, (await race.Response!).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("MSG-ACCOUNT-07", await login.ErrorCodeAsync());
        var sessions = await factory.QueryAsync(db => db.LoginSessions.AsNoTracking().Where(s => s.AccountId == staff.Id).ToListAsync());
        Assert.All(sessions, s => Assert.NotNull(s.RevokedAt));
        Assert.False(await factory.QueryAsync(db => db.LoginSessions.AnyAsync(s => s.AccountId == staff.Id && s.RevokedAt == null)));
    }

    private async Task<HttpClient> OwnerClientAsync() => await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username, Password);

    private async Task<Account> StaffAsync()
    {
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, null);
        return staff;
    }

    /// <summary>Locks the account once, when the login is about to store its session.</summary>
    private sealed class LockRace(Func<Task<HttpResponseMessage>> lockAccount)
    {
        private bool _armed;

        public Task<HttpResponseMessage>? Response { get; private set; }

        public void Arm() => _armed = true;

        public async Task RunAsync()
        {
            if (!_armed || Response is not null)
            {
                return;
            }

            Response = lockAccount();
            await Response;
        }
    }

    private sealed class LockBeforeSessionSaveUnitOfWork(OmtsDbContext db, LockRace race) : IUnitOfWork
    {
        private readonly UnitOfWork _inner = new(db, new PushOutbox());

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (db.ChangeTracker.Entries<LoginSession>().Any(e => e.State == EntityState.Added))
            {
                await race.RunAsync();
            }

            return await _inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => _inner.ReloadAsync(entity, cancellationToken);

        public void DiscardChanges() => _inner.DiscardChanges();

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => _inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => _inner.MarkChanged(entity);
    }
}
