using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Models.Storage;
using OMTS.Application.Features.Files.Models;
using OMTS.Application.Features.Files;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>Cloud Storage Service that is down: every upload fails (MSG05 path).</summary>
internal sealed class FailingFileStorage : IFileStorage
{
    public Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default) =>
        Task.FromResult(FileUploadResult.Failed("TEST_STORAGE_DOWN"));

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);

    public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default) =>
        Task.FromResult("about:blank");

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<IReadOnlyList<StoredFile>> ListAsync(string category, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoredFile>>([]);
}
