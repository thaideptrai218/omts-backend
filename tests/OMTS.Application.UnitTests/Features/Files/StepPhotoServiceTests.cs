using Microsoft.Extensions.Logging.Abstractions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;
using OMTS.Domain.Common;

namespace OMTS.Application.UnitTests.Features.Files;

public class StepPhotoServiceTests
{
    private const string Category = "step-photos";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    private static readonly byte[] Pdf = "%PDF-1.7\n%..."u8.ToArray();

    [Fact]
    public async Task Zero_photos_is_refused_on_field_Photos_and_stores_nothing()
    {
        var (service, storage) = Create();

        var result = await service.StoreAsync(Category, [], CancellationToken.None);

        AssertPhotosError(result);
        Assert.Empty(storage.Stored);
    }

    [Fact]
    public async Task One_and_three_photos_are_stored_through_the_file_storage_and_read_back()
    {
        var (service, storage) = Create();

        var one = await service.StoreAsync(Category, [File(Jpeg)], CancellationToken.None);
        var three = await service.StoreAsync(Category, [File(Jpeg), File(Png), File(Jpeg)], CancellationToken.None);

        Assert.True(one.IsSuccess);
        Assert.Single(one.Value!);
        Assert.True(three.IsSuccess);
        Assert.Equal(3, three.Value!.Count);
        Assert.Equal(4, storage.Stored.Count);
        Assert.All(three.Value!, photo =>
        {
            Assert.NotEqual(Guid.Empty, photo.Id);
            Assert.StartsWith($"{Category}/", photo.Key);
            Assert.EndsWith(photo.Key, photo.Url);
            Assert.Contains(photo.Key, storage.Stored.Keys);
        });
        Assert.Equal(3, three.Value!.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task Four_photos_are_refused_and_nothing_is_stored()
    {
        var (service, storage) = Create();

        var result = await service.StoreAsync(Category, [File(Jpeg), File(Jpeg), File(Png), File(Png)], CancellationToken.None);

        AssertPhotosError(result);
        Assert.Empty(storage.Stored);
    }

    [Fact]
    public async Task Wrong_type_is_refused_by_content_not_by_file_name()
    {
        var (service, storage) = Create();

        var pdf = await service.StoreAsync(Category, [File(Pdf, "photo.jpg")], CancellationToken.None);
        var mixed = await service.StoreAsync(Category, [File(Jpeg), File(Pdf)], CancellationToken.None);

        AssertPhotosError(pdf);
        AssertPhotosError(mixed);
        Assert.Empty(storage.Stored);
    }

    [Fact]
    public async Task Photo_over_10_MB_is_refused()
    {
        var (service, storage) = Create();
        var big = new IncomingFile("big.jpg", FileRules.TenMegabytes + 1, new MemoryStream(Jpeg));

        var result = await service.StoreAsync(Category, [big], CancellationToken.None);

        AssertPhotosError(result);
        Assert.Empty(storage.Stored);
    }

    [Fact]
    public async Task Edit_keeps_the_final_count_between_one_and_three()
    {
        var (service, storage) = Create();

        // Two kept, one added: three. Three kept, one added: four. One kept, nothing added: one. Remove all and add nothing: zero.
        Assert.True((await service.StoreAsync(Category, [File(Jpeg)], CancellationToken.None, kept: 2)).IsSuccess);
        AssertPhotosError(await service.StoreAsync(Category, [File(Jpeg)], CancellationToken.None, kept: 3));
        Assert.True((await service.StoreAsync(Category, [], CancellationToken.None, kept: 1)).IsSuccess);
        AssertPhotosError(await service.StoreAsync(Category, [], CancellationToken.None, kept: 0));
        Assert.Single(storage.Stored);
    }

    [Fact]
    public async Task Optional_photos_allow_none()
    {
        var (service, _) = Create();

        var result = await service.StoreAsync(Category, [], CancellationToken.None, minimum: 0);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task Unknown_kept_photo_or_other_file_part_is_refused()
    {
        var (service, _) = Create();

        AssertPhotosError(await service.StoreAsync(Category, [File(Jpeg)], CancellationToken.None, keysAreKnown: false));
        AssertPhotosError(await service.StoreAsync(Category, [File(Jpeg)], CancellationToken.None, otherFilePart: true));
    }

    [Fact]
    public async Task A_failed_upload_removes_the_photos_stored_before_it()
    {
        var (service, storage) = Create(failOnUploadNumber: 2);

        var result = await service.StoreAsync(Category, [File(Jpeg), File(Png)], CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.BadGateway, result.Error!.Kind);
        Assert.Empty(storage.Stored);
    }

    private static void AssertPhotosError<T>(Result<T> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal(StepPhotoService.Field, result.Error.Details!["field"]);
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Details["hint"] as string));
    }

    private static IncomingFile File(byte[] content, string name = "photo.jpg") => new(name, content.Length, new MemoryStream(content));

    private static (StepPhotoService Service, FakeFileStorage Storage) Create(int? failOnUploadNumber = null)
    {
        var storage = new FakeFileStorage(failOnUploadNumber);
        var saver = new UploadedFileSaver(storage, new NoReferences(), new NoUnitOfWork(), NullLogger<UploadedFileSaver>.Instance);
        return (new StepPhotoService(storage, new RecordPhotoStore(storage, saver)), storage);
    }

    /// <summary>In-memory storage with the contract of <see cref="IFileStorage"/>: a failed upload is returned, never thrown.</summary>
    private sealed class FakeFileStorage(int? failOnUploadNumber) : IFileStorage
    {
        private int _uploads;

        public Dictionary<string, byte[]> Stored { get; } = [];

        public Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default)
        {
            if (++_uploads == failOnUploadNumber)
            {
                return Task.FromResult(FileUploadResult.Failed("STORAGE_ERROR"));
            }

            var key = $"{category}/2026/11/{Guid.NewGuid():N}{FileValidator.ExtensionOf(file.Format)}";
            using var copy = new MemoryStream();
            file.File.Content.CopyTo(copy);
            Stored[key] = copy.ToArray();
            return Task.FromResult(FileUploadResult.Stored(key));
        }

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(Stored.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null);

        public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult($"https://files.test/{key}");

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Stored.Remove(key));

        public Task<IReadOnlyList<StoredFile>> ListAsync(string category, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredFile>>(Stored.Keys.Select(k => new StoredFile(k, DateTimeOffset.UtcNow)).ToList());
    }

    private sealed class NoReferences : IStoredFileReferenceQuery
    {
        public Task<IReadOnlySet<string>> ReferencedKeysAsync(string category, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<bool> IsReferencedAsync(string key, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class NoUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void DiscardChanges()
        {
        }

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion)
        {
        }

        public void MarkChanged(AuditableEntity entity)
        {
        }
    }
}
