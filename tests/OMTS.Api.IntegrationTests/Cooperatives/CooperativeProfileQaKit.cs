using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// A clock that follows the system clock until <see cref="Set"/> stops it at an instant. Tests log in first (the access
/// token is checked against the system clock), then move the business clock of the API to the instant they need.
/// </summary>
public sealed class MovableClock : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset? _instant;

    public void Set(DateTimeOffset instant)
    {
        lock (_gate)
        {
            _instant = instant;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _instant ?? base.GetUtcNow();
        }
    }
}

/// <summary>The API (in-memory database) whose <see cref="TimeProvider"/> is a <see cref="MovableClock"/>.</summary>
public sealed class MovableClockApiFactory : AuthApiFactory
{
    public MovableClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>Shared values of the QA tests of OMTS-217 PR 0.7 (FARM-01 v2).</summary>
internal static class CooperativeProfileQaKit
{
    public const string Profile = "/api/cooperative/profile";
    public const string CoverPhoto = "/api/cooperative/profile/cover-photo";
    public const string CoverPhotoFiles = "/api/public/cooperative/cover-photos/";
    public const string LogoFiles = "/api/public/cooperative/logos/";

    public static ConcurrencyConflictException Conflict => new(new InvalidOperationException("Test: changed by another request."));

    public static InvalidOperationException DatabaseDown => new("Test: the database is down.");

    /// <summary>The first business midnight (00:00 UTC+7) after <paramref name="instant"/>, as a UTC instant (17:00Z).</summary>
    public static DateTimeOffset NextBusinessMidnight(DateTimeOffset instant)
    {
        var tomorrow = BusinessClock.ToBusinessDate(instant).AddDays(1);
        return new DateTimeOffset(tomorrow.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset).ToUniversalTime();
    }

    /// <summary>Every file below the host's storage folder, as storage keys, in order.</summary>
    public static List<string> StoredKeys(AuthApiFactory api, string category)
    {
        var folder = Path.Combine(api.StorageRoot, category);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(api.StorageRoot, f).Replace('\\', '/')).Order().ToList()
            : [];
    }

    public static Task<string?> CoverKeyAsync(AuthApiFactory api) =>
        api.QueryAsync(db => db.Cooperatives.Select(c => c.CoverPhotoUrl).SingleAsync());

    public static Task<string?> CertificateKeyAsync(AuthApiFactory api, Guid id) =>
        api.QueryAsync(db => db.CooperativeCertifications.Where(c => c.Id == id).Select(c => c.FileUrl).SingleAsync());
}
