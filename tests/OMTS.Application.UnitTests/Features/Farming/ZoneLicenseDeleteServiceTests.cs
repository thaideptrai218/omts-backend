using Microsoft.Extensions.Logging.Abstractions;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Application.Features.Files;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>Deleting a license: the file and the record go together, the audit row is written, and the refusals leave everything as it was.</summary>
public class ZoneLicenseDeleteServiceTests
{
    private static readonly byte[] Version = [1, 2, 3, 4, 5, 6, 7, 8];

    private sealed class Setup
    {
        public ZoneComplianceDocument? Stored { get; set; }

        public ZoneComplianceDocument? Replacement { get; set; }

        public List<ZoneComplianceDocument> Removed { get; } = [];

        public List<string> DeletedFiles { get; } = [];

        public int Saves { get; private set; }

        public FakeAuditEventRepository Audit { get; } = new();

        public ZoneLicenseService Service()
        {
            var storage = StubProxy.Create<IFileStorage>(h => h["DeleteAsync"] = a =>
            {
                DeletedFiles.Add((string)a[0]!);
                return true;
            });
            var unitOfWork = StubProxy.Create<IUnitOfWork>(h =>
            {
                h["SaveChangesAsync"] = _ =>
                {
                    Saves++;
                    return 1;
                };
                h["MarkChanged"] = _ => null;
                h["ExpectRowVersion"] = _ => null;
            });
            var licenses = StubProxy.Create<IZoneLicenseRepository>(h =>
            {
                h["GetForEditAsync"] = a => Stored is { } d && d.Id == (Guid)a[0]! ? d : null;
                h["FindReplacementAsync"] = _ => Replacement;
                h["Remove"] = a =>
                {
                    Removed.Add((ZoneComplianceDocument)a[0]!);
                    return null;
                };
            });
            var accounts = StubProxy.Create<IAccountRepository>(h => h["GetByIdAsync"] = _ => null);
            var catalog = new FakeStatusCatalog();
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 27, 3, 0, 0, TimeSpan.Zero));
            return new ZoneLicenseService(
                licenses,
                StubProxy.Create<IFarmingZoneRepository>(_ => { }),
                storage,
                new UploadedFileSaver(storage, StubProxy.Create<IStoredFileReferenceQuery>(_ => { }), unitOfWork, NullLogger<UploadedFileSaver>.Instance),
                catalog,
                new RowVersionGuard(accounts, unitOfWork),
                new AuditLogService(Audit, new FakeCurrentUser(Guid.NewGuid()), catalog, clock),
                unitOfWork,
                clock);
        }
    }

    private static ZoneComplianceDocument License(int zoneStatus = StatusIds.Usage.InUse, string? file = "licenses/gp-1.pdf", Guid? replaces = null) => new()
    {
        DocumentNumber = "GP-1",
        EvidenceFileUrl = file,
        ReplacesDocumentId = replaces,
        RowVersion = Version,
        Zone = new FarmingZone { Code = "KV01", Name = "Zone 1", StatusId = zoneStatus },
    };

    private static string Current => RowVersionGuard.Encode(Version);

    [Fact]
    public async Task A_license_outside_a_chain_is_deleted_with_its_file_and_an_audit_row()
    {
        var setup = new Setup { Stored = License() };

        var result = await setup.Service().DeleteAsync(setup.Stored!.Id, Current, default);

        Assert.True(result.IsSuccess);
        Assert.Same(setup.Stored, Assert.Single(setup.Removed));
        Assert.Equal(["licenses/gp-1.pdf"], setup.DeletedFiles);
        Assert.Equal(1, setup.Saves);
        var audit = Assert.Single(setup.Audit.Added);
        Assert.Equal("ZoneLicense.Deleted", audit.Action);
        Assert.Equal(setup.Stored.Id, audit.RecordId);
        Assert.Equal("GP-1", audit.RecordCode);
        Assert.Contains("GP-1", audit.Before);
        Assert.Null(audit.After);
    }

    [Fact]
    public async Task A_license_without_a_file_is_deleted_and_no_file_is_touched()
    {
        var setup = new Setup { Stored = License(file: null) };

        var result = await setup.Service().DeleteAsync(setup.Stored!.Id, Current, default);

        Assert.True(result.IsSuccess);
        Assert.Empty(setup.DeletedFiles);
        Assert.Single(setup.Removed);
    }

    [Fact]
    public async Task An_unknown_license_is_not_found()
    {
        var setup = new Setup { Stored = License() };

        var result = await setup.Service().DeleteAsync(Guid.NewGuid(), Current, default);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task A_replaced_license_answers_409_gen09_and_nothing_is_removed()
    {
        var setup = new Setup { Stored = License() };
        setup.Replacement = new ZoneComplianceDocument { DocumentNumber = "GP-2", ReplacesDocumentId = setup.Stored.Id };

        var result = await setup.Service().DeleteAsync(setup.Stored.Id, Current, default);

        AssertRefused(setup, result, "MSG-GEN-09");
        Assert.Equal(setup.Replacement.Id, result.Error!.Details!["replacementId"]);
    }

    [Fact]
    public async Task A_license_that_renewed_another_answers_409_gen09_and_nothing_is_removed()
    {
        var setup = new Setup { Stored = License(replaces: Guid.NewGuid()) };

        var result = await setup.Service().DeleteAsync(setup.Stored.Id, Current, default);

        AssertRefused(setup, result, "MSG-GEN-09");
    }

    [Fact]
    public async Task A_license_of_a_zone_that_is_not_in_use_answers_409_gen09_and_nothing_is_removed()
    {
        var setup = new Setup { Stored = License(zoneStatus: StatusIds.Usage.Discontinued) };

        var result = await setup.Service().DeleteAsync(setup.Stored.Id, Current, default);

        AssertRefused(setup, result, "MSG-GEN-09");
    }

    [Fact]
    public async Task A_stale_row_version_answers_409_msg06_and_nothing_is_removed()
    {
        var setup = new Setup { Stored = License() };

        var result = await setup.Service().DeleteAsync(setup.Stored.Id, RowVersionGuard.Encode([9, 9, 9, 9, 9, 9, 9, 9]), default);

        AssertRefused(setup, result, "MSG06");
    }

    [Fact]
    public async Task A_missing_row_version_is_invalid_and_nothing_is_removed()
    {
        var setup = new Setup { Stored = License() };

        var result = await setup.Service().DeleteAsync(setup.Stored.Id, null, default);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Empty(setup.Removed);
        Assert.Empty(setup.DeletedFiles);
    }

    private static void AssertRefused(Setup setup, Result<bool> result, string code)
    {
        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal(code, result.Error.Message.Code);
        Assert.Empty(setup.Removed);
        Assert.Empty(setup.DeletedFiles);
        Assert.Empty(setup.Audit.Added);
        Assert.Equal(0, setup.Saves);
    }
}
