using System.Security.Cryptography;
using System.Text;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Traceability.Tracing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Traceability;

/// <summary>TRACE-02: the backward trace (BR-TRACE-03), what it misses (MSG-TRACE-02), the forward trace by invoice, the TRACE-03 marks, the commands and the export rows.</summary>
public sealed class LotTraceBuilderTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 2, 0, 0, TimeSpan.Zero);
    private static readonly FarmingZone Zone = new() { Code = "KV01", Name = "Zone one" };
    private static readonly Guid OtherZoneId = Guid.NewGuid();

    private sealed class Names : IStatusCatalog
    {
        public string CodeOf(int statusId) => $"S{statusId}";

        public string DisplayNameOf(int statusId) => $"Status {statusId}";
    }

    private static AccountAccess Staff(FixedRole role, Guid? zone) =>
        new(Guid.NewGuid(), SubjectType.Staff, false, true, [new ZoneGrant(zone, Guid.NewGuid(), role.Code, role.Name, role.GrantedPermissions)]);

    private static AccountAccess Owner() => new(Guid.NewGuid(), SubjectType.CooperativeOwner, false, true, []);

    /// <summary>A lot of lanes 1 to 3 of raft KV01-B01; lane 2 was hung twice, lane 3 never.</summary>
    private static (HarvestLot Lot, List<SeedingRecord> Sessions) Scenario(int secondReceiptStatus = StatusIds.PurchaseReceipt.Completed)
    {
        var raft = new Raft { Code = "KV01-B01", Zone = Zone, ZoneId = Zone.Id };
        var lanes = Enumerable.Range(1, 3).Select(n => new Lane { LaneNumber = n, Raft = raft }).ToList();
        var cycle = new CultivationCycle { Raft = raft, StartDate = new DateOnly(2026, 6, 1), StatusId = StatusIds.CultivationCycle.Open };
        var lot = new HarvestLot
        {
            LotCode = "KV01-B01-20261108", Raft = raft, RaftId = raft.Id, Cycle = cycle, CycleId = cycle.Id, HarvestedAt = At, HarvestDate = new DateOnly(2026, 11, 8), CageCount = 8,
            EstimatedKg = 400m, StatusId = StatusIds.HarvestLot.Open,
        };
        lot.Lanes = lanes.Select(l => new HarvestLotLane { Lane = l, LaneId = l.Id, IsFinished = l.LaneNumber == 1 }).ToList();
        var supplier = new Supplier { Name = "DEMO seed farm" };
        SeedingRecord Session(DateOnly date, string receiptCode, int status, params Lane[] on)
        {
            var receipt = new PurchaseReceipt { Code = receiptCode, Supplier = supplier, SupplierId = supplier.Id, ReceiptDate = date.AddDays(-1), StatusId = status };
            var line = new PurchaseReceiptLine { Receipt = receipt, ReceiptId = receipt.Id, LineNumber = 1, Unit = "kg", Quantity = 50m };
            var session = new SeedingRecord { Cycle = cycle, SeedingDate = date, SeedLotLine = line, Quantity = 20m };
            session.Lanes = on.Select(l => new SeedingRecordLane { SeedingRecord = session, LaneId = l.Id, Lane = l }).ToList();
            return session;
        }

        // Listed newest first, as the repository reads them: the trace shows them the earliest first.
        return (lot, [
            Session(new DateOnly(2026, 7, 1), "PN000002", secondReceiptStatus, lanes[1]),
            Session(new DateOnly(2026, 6, 2), "PN000001", StatusIds.PurchaseReceipt.Completed, lanes[0], lanes[1]),
        ]);
    }

    private static LotSaleLine Line(string invoice, Guid zoneId, int number, decimal kg, string? customer = "DEMO trader") =>
        new(new Guid(MD5.HashData(Encoding.UTF8.GetBytes(invoice))), invoice, At, new DateOnly(2026, 11, 8), zoneId, "KV01",
            customer is null ? null : Guid.Empty, customer, customer is null ? null : "999900000001", number, "Oysters", kg, 30_000m, kg * 30_000m);

    private static LotTrace Build(HarvestLot lot, List<SeedingRecord> sessions, AccountAccess? access, IReadOnlyList<LotSaleLine>? lines = null,
        IReadOnlyList<TraceCorrectionRow>? corrections = null) =>
        LotTraceBuilder.Build(new LotTraceSource(lot, sessions, lines ?? [], corrections ?? [], new Dictionary<Guid, string>()), access, new Names());

    [Fact]
    public void The_backward_trace_shows_every_session_of_each_lane_and_names_what_is_missing()
    {
        var (lot, sessions) = Scenario(secondReceiptStatus: StatusIds.PurchaseReceipt.Cancelled);

        var trace = Build(lot, sessions, Owner());

        Assert.Equal([1, 2, 3], trace.Lanes.Select(l => l.LaneNumber));
        Assert.Equal(["PN000001-1"], trace.Lanes[0].Seedings.Select(s => s.SeedLot.Code));
        // BR-CYCLE-02: lane 2 hung again shows both seed lots, the earliest first; one on a cancelled receipt is not enough to be missing.
        Assert.Equal([("PN000001-1", true), ("PN000002-1", false)], trace.Lanes[1].Seedings.Select(s => (s.SeedLot.Code, s.OnCompletedReceipt)));
        Assert.Empty(trace.Lanes[2].Seedings);
        Assert.Equal(("DEMO seed farm", "PN000001"), (trace.Lanes[0].Seedings[0].SeedLot.SupplierName, trace.Lanes[0].Seedings[0].SeedLot.ReceiptCode));
        var gap = Assert.Single(trace.Missing);
        Assert.Equal((OriginGapKind.NoSeedingSession, (int?)3), (gap.Kind, gap.LaneNumber));
        Assert.Equal(("MSG-TRACE-02",
            "Missing data in the trace of harvest lot KV01-B01-20261108: lane 3 has no seeding session in the cycle of the lot. Please ask the Cooperative Owner whether the data must be corrected."),
            (trace.MessageCode, trace.Message));
    }

    [Fact]
    public void A_complete_trace_has_no_message()
    {
        var (lot, sessions) = Scenario();
        lot.Lanes = lot.Lanes.Where(l => l.Lane.LaneNumber < 3).ToList();

        var trace = Build(lot, sessions, Owner());

        Assert.Empty(trace.Missing);
        Assert.Null(trace.Message);
        Assert.Null(trace.MessageCode);
    }

    // BR-TRACE-03: the lines of one invoice together; the prices only for whoever views the invoices of the invoice's zone (BR-SALE-08: not always the lot's zone).
    [Fact]
    public void The_forward_trace_groups_the_lines_by_invoice_and_hides_prices_outside_the_callers_invoice_zones()
    {
        var (lot, sessions) = Scenario();
        IReadOnlyList<LotSaleLine> lines = [Line("HD000001", Zone.Id, 1, 10m), Line("HD000001", Zone.Id, 2, 5m), Line("HD000002", OtherZoneId, 1, 7m, customer: null)];

        var manager = Build(lot, sessions, Staff(FixedRoles.ZoneManager, Zone.Id), lines);
        var owner = Build(lot, sessions, Owner(), lines);

        Assert.Equal([("HD000001", 15m, false), ("HD000002", 7m, true)], manager.Invoices.Select(i => (i.InvoiceCode, i.Kg, i.WalkIn)));
        Assert.Equal(22m, manager.InvoicedKg);
        Assert.Equal([30_000m, 30_000m], manager.Invoices[0].Lines.Select(l => l.UnitPrice!.Value));
        Assert.Equal([(decimal?)null], manager.Invoices[1].Lines.Select(l => l.UnitPrice));
        Assert.Null(manager.Invoices[1].Lines[0].Amount);
        Assert.Equal(210_000m, owner.Invoices[1].Lines[0].Amount);
        Assert.All(owner.Invoices, i => Assert.Empty(i.Returns));
    }

    [Fact]
    public void Correction_marks_go_to_the_lot_and_to_the_sessions_they_corrected()
    {
        var (lot, sessions) = Scenario();
        var actor = Guid.NewGuid();
        var rows = new List<TraceCorrectionRow>
        {
            new(sessions[1].Id, At.AddDays(1), actor, "Wrong seed lot"),
            new(lot.Id, At.AddDays(2), null, "Wrong cage count"),
        };

        var trace = LotTraceBuilder.Build(new LotTraceSource(lot, sessions, [], rows, new Dictionary<Guid, string> { [actor] = "DEMO manager" }), Owner(), new Names());

        Assert.Equal([("DEMO manager", "Wrong seed lot")], trace.Lanes[0].Seedings[0].Corrections.Select(m => (m.ActorName, m.Reason)));
        Assert.Empty(trace.Lanes[1].Seedings[1].Corrections);
        Assert.Equal([((string?)null, (string?)"Wrong cage count")], trace.Corrections.Select(m => (m.ActorName, m.Reason)));
        Assert.Equal("09/11/2026 09:00 DEMO manager: Wrong seed lot", LotTraceExport.Corrections(trace.Lanes[0].Seedings[0].Corrections));
    }

    // §I.4.4: the Accountant views (R), exports nothing, publishes and corrects nothing; the Zone Manager does all in the zone; a lot on no invoice is not offered for publishing.
    [Fact]
    public void The_commands_follow_the_role_and_the_zone()
    {
        var (lot, sessions) = Scenario();
        IReadOnlyList<LotSaleLine> sold = [Line("HD000001", Zone.Id, 1, 10m)];

        var accountant = Build(lot, sessions, Staff(FixedRoles.Accountant, null), sold);
        var manager = Build(lot, sessions, Staff(FixedRoles.ZoneManager, Zone.Id), sold);
        var unsold = Build(lot, sessions, Staff(FixedRoles.ZoneManager, Zone.Id));

        Assert.Equal((false, false, false), (accountant.CanPublish, accountant.CanCorrect, accountant.CanExport));
        Assert.Equal((true, true, true), (manager.CanPublish, manager.CanCorrect, manager.CanExport));
        Assert.False(unsold.CanPublish);
        Assert.Equal(30_000m, accountant.Invoices[0].Lines[0].UnitPrice);
    }

    [Fact]
    public void The_export_has_one_lot_row_one_row_per_lane_and_session_and_one_row_per_invoice_line()
    {
        var (lot, sessions) = Scenario();
        var trace = Build(lot, sessions, Owner(), [Line("HD000001", Zone.Id, 1, 10m), Line("HD000002", Zone.Id, 1, 4m, customer: null)]);

        var sheets = LotTraceExport.Sheets(trace);

        Assert.Equal(["Lot", "Backward", "Forward"], sheets.Select(s => s.Name));
        var lotRow = sheets[0].Rows.Single();
        Assert.Equal(("KV01-B01-20261108", "1, 2, 3", 14m), ((string)lotRow[0]!, (string)lotRow[8]!, (decimal)lotRow[15]!));
        Assert.Equal([(1, "PN000001-1"), (2, "PN000001-1"), (2, "PN000002-1"), (3, null)], sheets[1].Rows.Select(r => ((int)r[0]!, (string?)r[5])));
        Assert.Equal("lane 3 has no seeding session in the cycle of the lot", sheets[1].Rows[3][11]);
        Assert.Equal([("HD000001", "DEMO trader"), ("HD000002", "Walk-in customer")], sheets[2].Rows.Select(r => ((string)r[0]!, (string?)r[4])));
        Assert.Equal("trace-KV01-B01-20261108.xlsx", LotTraceExport.FileName(trace.LotCode));
        Assert.Equal("trace-KV_1.xlsx", LotTraceExport.FileName("KV/1"));
    }
}
