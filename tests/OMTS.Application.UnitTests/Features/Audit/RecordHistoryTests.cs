using System.Text.Json;
using System.Text.Json.Nodes;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Dtos;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Cultivation.MaterialUsages;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Traceability.Withdrawal;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.UnitTests.Features.Audit;

/// <summary>The Vietnamese labels and values of the history of a record: every action and every field written for the six record types has a name, and no value reads as English, JSON or an id.</summary>
public class RecordHistoryTests
{
    private static readonly IReadOnlyDictionary<Guid, string> NoReferences = new Dictionary<Guid, string>();

    private static JsonElement? Json(string json) => JsonSerializer.SerializeToElement(JsonNode.Parse(json));

    private static List<RecordHistoryChange> Changes(string recordType, string? before, string? after, IReadOnlyDictionary<Guid, string>? references = null) =>
        RecordHistoryService.Changes(recordType, before is null ? null : Json(before), after is null ? null : Json(after), references ?? NoReferences);

    private static IEnumerable<string> KeysOf(object snapshot) => JsonNode.Parse(AuditValueSerializer.Serialize(snapshot)!)!.AsObject().Select(p => p.Key);

    // The actions each record type writes (the constants of its services), with the type they are shown under.
    public static TheoryData<string, string> Actions => new()
    {
        { "HarvestLot", "HarvestLot.Created" }, { "HarvestLot", "HarvestLot.Corrected" }, { "HarvestLot", "HarvestLot.Removed" }, { "HarvestLot", "HarvestLot.Closed" },
        { "HarvestLot", "HarvestLot.Reopened" }, { "HarvestLot", "HarvestLot.Weighed" }, { "HarvestLot", "HarvestLot.Reweighed" }, { "HarvestLot", "HarvestLot.WeighingAgreed" },
        { "HarvestLot", "HarvestLot.LaneFinished" }, { "HarvestLot", "HarvestLot.TraceCorrected" }, { "HarvestLot", "HarvestLot.Published" }, { "HarvestLot", "HarvestLot.Republished" },
        { "HarvestLot", "HarvestLot.Withdrawn" },
        { "SeedingRecord", "SeedingRecord.Created" }, { "SeedingRecord", "SeedingRecord.Corrected" }, { "SeedingRecord", "SeedingRecord.Removed" }, { "SeedingRecord", "SeedingRecord.TraceCorrected" },
        { "MaterialUsage", "MaterialUsage.Created" }, { "MaterialUsage", "MaterialUsage.Updated" }, { "MaterialUsage", "MaterialUsage.Removed" },
        { "CultivationLog", "CultivationLog.Created" }, { "CultivationLog", "CultivationLog.Corrected" }, { "CultivationLog", "CultivationLog.Removed" }, { "CultivationLog", "CultivationLog.TraceCorrected" },
        { "CultivationCycle", "CultivationCycle.Started" }, { "CultivationCycle", "CultivationCycle.Updated" }, { "CultivationCycle", "CultivationCycle.Cancelled" },
        { "CultivationCycle", "CultivationCycle.Closed" }, { "CultivationCycle", "CultivationCycle.SeedingCompleted" },
        { "NurseryBatch", "NurseryBatch.Created" }, { "NurseryBatch", "NurseryBatch.Edited" }, { "NurseryBatch", "NurseryBatch.Cancelled" }, { "NurseryBatch", "NurseryBatch.Completed" },
        { "NurseryBatch", "NurseryBatch.SurvivorsCorrected" }, { "NurseryBatch", "NurseryBatch.Closed" }, { "NurseryBatch", "NurseryBatch.Reopened" }, { "NurseryBatch", "NurseryBatch.AllocationsChanged" },
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public void Every_action_of_a_record_type_has_a_Vietnamese_label(string recordType, string action)
    {
        var name = RecordHistoryLabels.ActionOf(action);

        Assert.True(RecordHistoryLabels.HasAction(name), $"{action} has no label");
        var label = RecordHistoryLabels.ActionLabel(name, recordType);
        Assert.NotEqual(RecordHistoryLabels.UnknownAction, label);
        Assert.NotEqual(name, label);
    }

    [Fact]
    public void An_action_nobody_named_is_Thay_doi_and_never_English()
    {
        Assert.Equal("Thay đổi", RecordHistoryLabels.ActionLabel("SomethingNew", "HarvestLot"));
        Assert.Equal("Thay đổi", RecordHistoryLabels.ActionLabel(RecordHistoryLabels.ActionOf("HarvestLot.Frobnicated")));
    }

    [Theory]
    [InlineData("HarvestLot", "Published", "Công khai truy xuất")]
    [InlineData("HarvestLot", "Closed", "Kết thúc lô")]
    [InlineData("CultivationCycle", "Closed", "Chốt vụ")]
    [InlineData("NurseryBatch", "Closed", "Đóng đợt ươm")]
    [InlineData("HarvestLot", "Reweighed", "Cân lại lô")]
    [InlineData("HarvestLot", "LaneFinished", "Kết thúc thu hoạch làn")]
    [InlineData("MaterialUsage", "Edited", "Chỉnh sửa")]
    public void The_label_of_an_action_follows_the_record_type(string recordType, string action, string expected) =>
        Assert.Equal(expected, RecordHistoryLabels.ActionLabel(action, recordType));

    [Fact]
    public void Corrected_and_Updated_are_Edited()
    {
        Assert.Equal("Edited", RecordHistoryLabels.ActionOf("HarvestLot.Corrected"));
        Assert.Equal("Edited", RecordHistoryLabels.ActionOf("MaterialUsage.Updated"));
        Assert.Equal("TraceCorrected", RecordHistoryLabels.ActionOf("HarvestLot.TraceCorrected"));
    }

    [Fact]
    public void Every_field_the_snapshots_write_has_a_label()
    {
        var raft = new Raft { Code = "B01" };
        var lot = new HarvestLot { LotCode = "LH01", Raft = raft };
        var cycle = new CultivationCycle { Raft = raft };
        var receipt = new PurchaseReceipt { Code = "PN000001" };
        var line = new PurchaseReceiptLine { Receipt = receipt, LineNumber = 1 };
        var seeding = new SeedingRecord { Cycle = cycle, SeedLotLine = line };
        var batch = new NurseryBatch { Code = "UG01", SeedLotLine = line };
        var log = new CultivationLog { Cycle = cycle };
        var usage = new MaterialUsage();
        var item = new FarmingInputItem { Code = "VT01" };

        var written = new (string RecordType, IEnumerable<string> Fields)[]
        {
            ("HarvestLot", KeysOf(HarvestLotAudit.Snapshot(lot, alwaysNameCrew: true))),
            ("HarvestLot", KeysOf(HarvestLotAudit.CloseFacts(lot, "CLOSED"))),
            ("HarvestLot", KeysOf(HarvestLotAudit.WeighingFacts(lot, "OPEN"))),
            ("SeedingRecord", KeysOf(SeedingValues.Snapshot(seeding))),
            ("MaterialUsage", KeysOf(MaterialUsageValues.Snapshot(usage, raft, item))),
            ("CultivationLog", KeysOf(CultivationLogValues.Snapshot(log))),
            ("CultivationCycle", KeysOf(CultivationCycleValues.Snapshot(cycle))),
            ("CultivationCycle", KeysOf(CultivationCycleValues.NurserySnapshot(cycle, "UG01", "Hàu"))),
            ("NurseryBatch", KeysOf(NurseryValues.Snapshot(batch))),
            // The values of the events that are not a snapshot of the record, as their services write them: publication, withdrawal, weighing agreed, lane finished, the close of a cycle,
            // the completion of the seeding and what a trace correction adds.
            ("HarvestLot", ["OriginState", "PublishedAt", "WithdrawnAt", "SalesInvoiceCode"]),
            ("HarvestLot", KeysOf(new WithdrawalAuditAfter("Withdrawn", null, [], []))),
            ("HarvestLot", ["WeighingAgreedKg", "WeighingAgreedFor", "TraderCages", "TraderCagesKg"]),
            ("HarvestLot", ["Lane", "IsFinished"]),
            ("HarvestLot", ["OwnerApprovalConfirmed"]),
            ("CultivationCycle", ["Status", "CloseDate", "Assessment", "ClosedEarly", "OwnerApprovalConfirmed", "SeedingCompletedOn", "SeededCount", "LaneCount", "Photos"]),
            ("SeedingRecord", ["OwnerApprovalConfirmed"]),
            ("CultivationLog", ["OwnerApprovalConfirmed"]),
            ("NurseryBatch", ["Photos", "Crew", "Status"]),
        };

        var missing = written.SelectMany(w => w.Fields.Where(f => f != "RowVersion" && f != "AffectedPublishedLotCodes" && !RecordHistoryLabels.HasField(w.RecordType, f)).Select(f => $"{w.RecordType}.{f}")).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_figure_of_the_close_of_a_cycle_has_a_label()
    {
        foreach (var figure in new[] { "SeedHung", "KgHarvested", "LastHarvestDate", "DaysOfFarming", "ExpectedSeedQuantity", "TargetYieldKg", "EarlyReason", "NotHarvestedLanes" })
        {
            Assert.NotNull(RecordHistoryLabels.SummaryField(figure));
        }
    }

    [Fact]
    public void A_publication_reads_in_Vietnamese_and_names_the_invoice_by_its_code()
    {
        var changes = Changes(
            "HarvestLot",
            """{"OriginState":"NotPublished","PublishedAt":null}""",
            """{"OriginState":"Published","PublishedAt":"2026-11-27T03:05:00+00:00","SalesInvoiceCode":"HD000003"}""");

        Assert.Equal(["Truy xuất nguồn gốc", "Thời điểm công khai", "Mã hóa đơn bán"], changes.Select(c => c.Label));
        Assert.Equal("Chưa công khai", changes[0].From);
        Assert.Equal("Đã công khai", changes[0].To);
        Assert.Null(changes[1].From);
        Assert.Equal("27/11/2026 10:05", changes[1].To);
        Assert.Equal("HD000003", changes[2].To);
    }

    [Fact]
    public void A_close_shows_status_reason_and_the_closing_invoice_by_its_code()
    {
        var invoice = Guid.NewGuid();
        var references = new Dictionary<Guid, string> { [invoice] = "HD000003" };

        var changes = Changes(
            "HarvestLot",
            """{"Status":"OPEN","CloseDate":null,"LeftoverKg":null,"CloseReason":null,"ClosedBySalesInvoiceId":null,"ReopenedOn":null}""",
            $$"""{"Status":"CLOSED","CloseDate":"2026-11-27","LeftoverKg":0,"CloseReason":"SoldWholeLot","ClosedBySalesInvoiceId":"{{invoice}}","ReopenedOn":null}""",
            references);

        Assert.Equal(("Trạng thái", "Đang mở", "Đã kết thúc"), (changes[0].Label, changes[0].From, changes[0].To));
        Assert.Equal("27/11/2026", changes.Single(c => c.Field == "CloseDate").To);
        Assert.Equal("Bán hết lô", changes.Single(c => c.Field == "CloseReason").To);
        var closing = changes.Single(c => c.Field == "ClosedBySalesInvoiceId");
        Assert.Equal("Hóa đơn chốt lô", closing.Label);
        Assert.Equal("HD000003", closing.To);
    }

    [Fact]
    public void An_id_that_cannot_be_named_is_not_shown()
    {
        var changes = Changes("HarvestLot", """{"Status":"OPEN","ClosedBySalesInvoiceId":null}""", $$"""{"Status":"CLOSED","ClosedBySalesInvoiceId":"{{Guid.NewGuid()}}"}""");

        Assert.Equal(["Status"], changes.Select(c => c.Field));
        Assert.DoesNotContain(changes, c => Guid.TryParse(c.To, out _) || Guid.TryParse(c.From, out _));
    }

    [Fact]
    public void A_creation_leaves_out_empty_values_and_has_no_from()
    {
        var changes = Changes(
            "HarvestLot",
            before: null,
            after: """{"LotCode":"LH01","HarvestDate":"2026-11-20","Lanes":[1,2],"CageCount":12.5,"EstimatedKg":1234.5,"OverPlanReason":null,"Photos":[],"Crew":[]}""");

        Assert.Equal(["LotCode", "HarvestDate", "Lanes", "CageCount", "EstimatedKg"], changes.Select(c => c.Field));
        Assert.All(changes, c => Assert.Null(c.From));
        Assert.Equal("20/11/2026", changes[1].To);
        Assert.Equal("1, 2", changes[2].To);
        Assert.Equal("12,5", changes[3].To);
        Assert.Equal("1.234,5", changes[4].To);
        Assert.Equal("kg", changes[4].Unit);
    }

    [Fact]
    public void An_empty_photo_field_never_reads_as_nothing_to_nothing()
    {
        Assert.Empty(Changes("HarvestLot", """{"Photos":[]}""", """{"Photos":[]}"""));
        Assert.Empty(Changes("SeedingRecord", before: null, after: """{"Photos":0}"""));
    }

    [Fact]
    public void Photos_are_counted_not_listed()
    {
        var changes = Changes("HarvestLot", """{"Photos":["a/1.jpg"]}""", """{"Photos":["a/1.jpg","a/2.jpg"]}""");

        var photos = Assert.Single(changes);
        Assert.Equal(("1 ảnh", "2 ảnh"), (photos.From, photos.To));
    }

    [Fact]
    public void A_removal_lists_what_the_record_held()
    {
        var changes = Changes("CultivationLog", """{"VisitDate":"2026-11-20","LogType":"FEEDING","Notes":"","Photos":[]}""", after: null);

        Assert.Equal(["VisitDate", "LogType"], changes.Select(c => c.Field));
        Assert.Equal("Cho ăn", changes[1].From);
        Assert.All(changes, c => Assert.Null(c.To));
    }

    [Fact]
    public void Flags_are_Co_and_Khong_and_an_unchanged_field_is_left_out()
    {
        var changes = Changes("HarvestLot", """{"IsFinished":false,"Lane":3}""", """{"IsFinished":true,"Lane":3}""");

        var finished = Assert.Single(changes);
        Assert.Equal(("Đã thu xong", "Không", "Có"), (finished.Label, finished.From, finished.To));
    }

    [Fact]
    public void Product_lines_name_the_product_and_show_cages_and_kg()
    {
        var product = Guid.NewGuid();
        var references = new Dictionary<Guid, string> { [product] = "Hàu sữa" };

        var changes = Changes(
            "HarvestLot",
            $$"""{"WeighedKg":null,"WeighedLines":[{"ProductId":"{{product}}","WeighedKg":null}]}""",
            $$"""{"WeighedKg":410.5,"WeighedLines":[{"ProductId":"{{product}}","WeighedKg":410.5}]}""",
            references);

        Assert.Equal("410,5", changes.Single(c => c.Field == "WeighedKg").To);
        Assert.Equal("Hàu sữa (410,5 kg)", changes.Single(c => c.Field == "WeighedLines").To);
    }

    [Fact]
    public void The_figures_of_a_close_are_fields_of_their_own()
    {
        var changes = Changes(
            "CultivationCycle",
            """{"Status":"OPEN"}""",
            """{"Status":"CLOSED","Summary":{"SeedHung":1000,"SeedUnit":"con","KgHarvested":2500,"EarlyReason":"LanesNotHarvested","NotHarvestedLanes":[2,3],"DaysOfFarming":120}}""");

        Assert.Equal("Đã chốt", changes.Single(c => c.Field == "Status").To);
        var seed = changes.Single(c => c.Field == "SeedHung");
        Assert.Equal(("1.000", "con"), (seed.To, seed.Unit));
        Assert.Equal("Còn làn chưa thu hoạch", changes.Single(c => c.Field == "EarlyReason").To);
        Assert.Equal("2, 3", changes.Single(c => c.Field == "NotHarvestedLanes").To);
        Assert.DoesNotContain(changes, c => c.Field is "Summary" or "SeedUnit");
    }

    [Fact]
    public void A_field_nobody_named_is_shown_under_a_Vietnamese_label_and_a_hidden_one_is_not_shown()
    {
        var changes = Changes("HarvestLot", """{"Surprise":"a","RowVersion":"x"}""", """{"Surprise":"b","RowVersion":"y","AffectedPublishedLotCodes":["LH01"]}""");

        var surprise = Assert.Single(changes);
        Assert.Equal("Thông tin khác", surprise.Label);
    }

    [Fact]
    public void A_reason_the_system_wrote_as_a_code_is_told_in_words_and_a_typed_one_stays()
    {
        Assert.Equal("Hóa đơn chốt lô đã bị hủy", RecordHistoryLabels.ReasonLabel("SalesInvoiceCancelled"));
        Assert.Equal("Cân nhầm", RecordHistoryLabels.ReasonLabel("Cân nhầm"));
        Assert.Null(RecordHistoryLabels.ReasonLabel(null));
    }

    [Theory]
    [InlineData("LogType", "OTHER", "Khác")]
    [InlineData("OriginState", "Withdrawn", "Đã rút công khai")]
    [InlineData("CloseReason", "TimeLimitReached", "Quá thời hạn bán")]
    public void Coded_values_are_told_in_Vietnamese(string field, string code, string expected) =>
        Assert.Equal(expected, RecordHistoryLabels.Value("HarvestLot", field, code));

    [Fact]
    public void A_withdrawal_names_the_traders_without_their_ids()
    {
        var changes = Changes(
            "HarvestLot",
            """{"OriginState":"Published","WithdrawnAt":null}""",
            $$"""{"OriginState":"Withdrawn","WithdrawnAt":"2026-11-27T01:00:00+00:00","NotifiedTraders":[{"CustomerId":"{{Guid.NewGuid()}}","CustomerName":"Anh Tư","InvoiceCodes":["HD000001","HD000002"],"SendingId":"{{Guid.NewGuid()}}","NotifiedInApp":true}],"WalkInInvoiceCodes":[]}""");

        Assert.Equal("Anh Tư (HD000001, HD000002)", changes.Single(c => c.Field == "NotifiedTraders").To);
        Assert.Equal("27/11/2026 08:00", changes.Single(c => c.Field == "WithdrawnAt").To);
        Assert.DoesNotContain(changes, c => c.Field == "WalkInInvoiceCodes");
    }
}
