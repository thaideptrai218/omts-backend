using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Domain.Common;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Storage;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>Switches the next save or the file removals of a <see cref="FileFaultApiFactory"/> host to fail.</summary>
public sealed class FileFaults
{
    private (Exception Failure, bool AfterCommit)? _nextSave;

    /// <summary>Every <see cref="IFileStorage.DeleteAsync"/> throws, as a provider bug would.</summary>
    public bool DeleteThrows { get; set; }

    /// <summary>The next save throws <paramref name="failure"/> and saves nothing.</summary>
    public void FailNextSave(Exception failure) => _nextSave = (failure, false);

    /// <summary>The next save is committed, then throws <paramref name="failure"/> (a reply lost after the commit).</summary>
    public void FailNextSaveAfterCommit(Exception failure) => _nextSave = (failure, true);

    internal (Exception Failure, bool AfterCommit)? TakeSaveFailure()
    {
        var next = _nextSave;
        _nextSave = null;
        return next;
    }
}

/// <summary>The real API (in-memory database, Local storage) whose unit of work and file removals can be made to fail.</summary>
public sealed class FileFaultApiFactory : AuthApiFactory
{
    public FileFaults Faults { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<UnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new FaultyUnitOfWork(provider.GetRequiredService<UnitOfWork>(), Faults));
            services.AddSingleton<IFileStorage>(provider => new FaultyDeleteStorage(provider.GetRequiredService<LocalFileStorage>(), Faults));
        });
    }

    private sealed class FaultyUnitOfWork(IUnitOfWork inner, FileFaults faults) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (faults.TakeSaveFailure() is not { } fault)
            {
                return await inner.SaveChangesAsync(cancellationToken);
            }

            if (fault.AfterCommit)
            {
                await inner.SaveChangesAsync(cancellationToken);
            }

            throw fault.Failure;
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);
    }

    private sealed class FaultyDeleteStorage(IFileStorage inner, FileFaults faults) : IFileStorage
    {
        public Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default) =>
            inner.UploadAsync(file, category, cancellationToken);

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) => inner.OpenReadAsync(key, cancellationToken);

        public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default) =>
            inner.GetReadUrlAsync(key, lifetime, cancellationToken);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            faults.DeleteThrows ? throw new IOException("Test: the file store refused the delete.") : inner.DeleteAsync(key, cancellationToken);

        public Task<IReadOnlyList<StoredFile>> ListAsync(string category, CancellationToken cancellationToken = default) =>
            inner.ListAsync(category, cancellationToken);
    }
}
