using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Features.Files;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;
using static OMTS.Api.IntegrationTests.Traceability.TraceCorrectionTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// QA of the multipart TRACE-03.1 and TRACE-03.2 saves (GFR-01, MSG-GEN-03): a refused or failed save stores no file and changes nothing, a removed photo leaves the store, and the
/// audit row keeps the photos before and after (GBR-02).
/// </summary>
public class TraceCorrectionsQaTestsFiles
{
    private const string LogPhotos = FileCategories.CultivationLogPhotos;
    private const string LotPhotos = FileCategories.HarvestLotPhotos;

    private static ConcurrencyConflictException Conflict => new(new InvalidOperationException("Test: changed by another request."));

    private static async Task<(TracedRaft Raft, Guid LogId, List<string> LogKeys)> ClosedAsync(AuthApiFactory api)
    {
        var t = await api.TracedRaftAsync();
        var log = await t.Owner.Client.RecordLogAsync(t.CycleId, new LogFields { Notes = "before" });
        await t.Owner.Client.CloseCycleAsync(t.CycleId);
        return (t, log.Id(), log.PhotoKeys());
    }

    [Fact]
    public async Task A_zone_managers_save_refused_for_msg_trace_04_stores_no_photo()
    {
        using var api = new AuthApiFactory();
        var (t, logId, keys) = await ClosedAsync(api);
        var (logFiles, lotFiles) = (StoredKeys(api, LogPhotos), StoredKeys(api, LotPhotos));

        await (await api.SendLotTraceAsync(t.Manager.Client, t.LotId, HarvestFields.OfLanes(1) with { Photos = [Jpeg] })).AgreementRefusalAsync();
        await (await t.Manager.Client.PutLogTraceAsync(logId, new LogFields { KeepPhotoKeys = keys, Photos = [Png], Reason = Reason })).AgreementRefusalAsync();

        Assert.Equal(logFiles, StoredKeys(api, LogPhotos));
        Assert.Equal(lotFiles, StoredKeys(api, LotPhotos));
        Assert.Empty((await api.LotRowAsync(t.LotId)).Photos);
        Assert.Equal(keys, (await t.Owner.Client.GetLogAsync(logId)).PhotoKeys());
    }

    [Fact]
    public async Task A_replaced_log_photo_leaves_the_store_and_the_audit_row_keeps_both_keys()
    {
        using var api = new AuthApiFactory();
        var (t, logId, keys) = await ClosedAsync(api);
        var old = Assert.Single(keys);

        var saved = await (await t.Manager.Client.PutLogTraceAsync(logId, new LogFields { Notes = "before", KeepPhotoKeys = [], Photos = [Png], Reason = Reason }, ownerApproved: true))
            .ReadAsync(HttpStatusCode.OK);

        var added = Assert.Single(saved.GetProperty("log").PhotoKeys());
        Assert.NotEqual(old, added);
        Assert.Equal([added], StoredKeys(api, LogPhotos));
        var row = Assert.Single(await api.AuditRowsAsync(LogAction, logId));
        Assert.Contains(old, row.Before);
        Assert.Contains(added, row.After);
        Assert.Equal(Reason, row.Reason);
    }

    [Fact]
    public async Task A_lot_photo_removed_by_a_correction_leaves_the_store()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, Png] });
        await raft.Owner.Client.CloseCycleAsync(raft.CycleId);
        var keys = lot.PhotoKeysOfLot();

        var saved = await (await api.SendLotTraceAsync(raft.Owner.Client, lot.Id(), HarvestFields.OfLanes(1), keep: [keys[1]])).ReadAsync(HttpStatusCode.OK);

        Assert.Equal([keys[1]], saved.GetProperty("lot").PhotoKeysOfLot());
        Assert.Equal([keys[1]], StoredKeys(api, LotPhotos));
        Assert.Contains(keys[0], Assert.Single(await api.AuditRowsAsync(LotAction, lot.Id())).Before);
    }

    [Fact]
    public async Task An_upload_that_fails_saves_nothing_and_leaves_no_file()
    {
        using var api = new FileFaultApiFactory();
        var (t, logId, keys) = await ClosedAsync(api);
        var (logFiles, lotFiles) = (StoredKeys(api, LogPhotos), StoredKeys(api, LotPhotos));

        api.Faults.UploadsBeforeFailure = 1;
        var log = await (await t.Owner.Client.PutLogTraceAsync(logId, new LogFields { Notes = "after", KeepPhotoKeys = keys, Photos = [Jpeg, Png], Reason = Reason }))
            .ReadAsync(HttpStatusCode.BadGateway);
        api.Faults.UploadsBeforeFailure = 1;
        var lot = await (await api.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9", Photos = [Jpeg, Png] })).ReadAsync(HttpStatusCode.BadGateway);
        api.Faults.UploadsBeforeFailure = null;

        Assert.Equal(("MSG-GEN-03", "MSG-GEN-03"), (log.Text("code"), lot.Text("code")));
        Assert.Equal(logFiles, StoredKeys(api, LogPhotos));
        Assert.Equal(lotFiles, StoredKeys(api, LotPhotos));
        Assert.Equal("before", (await t.Owner.Client.GetLogAsync(logId)).Text("notes"));
        Assert.Equal(8, (await api.LotRowAsync(t.LotId)).CageCount);
        Assert.Empty(await api.AuditRowsAsync(LogAction, logId));
        Assert.Empty(await api.AuditRowsAsync(LotAction, t.LotId));
    }

    [Fact]
    public async Task A_save_that_fails_after_the_uploads_answers_msg06_removes_them_and_keeps_the_old_photos()
    {
        using var api = new FileFaultApiFactory();
        var (t, logId, keys) = await ClosedAsync(api);
        var (logFiles, lotFiles) = (StoredKeys(api, LogPhotos), StoredKeys(api, LotPhotos));

        api.Faults.FailNextSave(Conflict);
        Assert.Equal("MSG06", await (await t.Owner.Client.PutLogTraceAsync(logId, new LogFields { Notes = "after", KeepPhotoKeys = [], Photos = [Png], Reason = Reason })).ErrorCodeAsync());
        api.Faults.FailNextSave(Conflict);
        Assert.Equal("MSG06", await (await api.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9", Photos = [Png] })).ErrorCodeAsync());

        Assert.Equal(logFiles, StoredKeys(api, LogPhotos));
        Assert.Equal(lotFiles, StoredKeys(api, LotPhotos));
        var read = await t.Owner.Client.GetLogAsync(logId);
        Assert.Equal("before", read.Text("notes"));
        Assert.Equal(keys, read.PhotoKeys());
        Assert.Empty(await api.AuditRowsAsync(LogAction, logId));
        Assert.Empty(await api.AuditRowsAsync(LotAction, t.LotId));
    }
}
