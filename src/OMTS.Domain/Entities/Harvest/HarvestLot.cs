using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Harvest;

/// <summary>
/// The oysters of one harvest trip to one raft (BR-HARVEST-03), with the whole lanes harvested and the estimated kg. Its code and harvest time never change.
/// It waits for its weighing (AwaitingWeighing), is for sale while Open or Reopened (BR-HARVEST-04) and gives its weighed kg to the product ledger. The estimated kg is for following the harvest and limits no sale (BR-HARVEST-02).
/// A lot may be recorded from a line of a harvest plan and may be split into the products it holds.
/// </summary>
public class HarvestLot : AuditableEntity, IHasStatus
{
    /// <summary>The most photos a lot holds (default of the PM question on the cap, as for a cultivation log). Photos are optional.</summary>
    public const int MaxPhotos = 10;

    /// <summary>Public, unique in the cooperative and never changed: KV01-B01-20261026, then -2, -3 on the same day.</summary>
    public string LotCode { get; set; } = string.Empty;

    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    /// <summary>The cycle Open on the harvest date (BR-CYCLE-01).</summary>
    public Guid CycleId { get; set; }
    public CultivationCycle Cycle { get; set; } = null!;

    /// <summary>Set by the system when the harvest is confirmed; never changed.</summary>
    public DateTimeOffset HarvestedAt { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="HarvestedAt"/>.</summary>
    public DateOnly HarvestDate { get; set; }

    /// <summary>Cages harvested on the trip, in steps of 0.01 (half a cage is 0.5): the Owner or Zone Manager counts them, so a part of a cage is allowed.</summary>
    public decimal CageCount { get; set; }

    /// <summary>A copy of the average kg per cage at record time; later settings do not change it (BR-HARVEST-02).</summary>
    public decimal KgPerCage { get; set; }

    /// <summary>Cages times <see cref="KgPerCage"/> unless the Zone Manager changed it (BR-HARVEST-02).</summary>
    public decimal EstimatedKg { get; set; }

    /// <summary>The kg of the weighing: the sum of the weighed kg of the lines, or the kg typed for a lot with no lines; null until the lot is weighed. The figures of the lot count from it once it is set.</summary>
    public decimal? WeighedKg { get; set; }

    /// <summary>When the lot was weighed (typed, or the time of the weighing); the latest weighing counts.</summary>
    public DateTimeOffset? WeighedAt { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="WeighedAt"/>: the days before automatic close count from it.</summary>
    public DateOnly? WeighedOn { get; set; }

    /// <summary>Who weighed the lot last.</summary>
    public Guid? WeighedByAccountId { get; set; }

    /// <summary>Why the cycle harvested kg went above its target yield, when the Zone Manager confirmed it (over 110 % of the target); null otherwise.</summary>
    public string? OverPlanReason { get; set; }

    /// <summary>Who recorded it: a Zone Manager corrects or removes only the lots they recorded (BR-HARVEST-05).</summary>
    public Guid RecordedByAccountId { get; set; }

    /// <summary>
    /// Kept for the lots that already carry a purchase request. A new lot does not set it and nothing requires it: the lot is tied to the harvest plan line it was recorded from.
    /// </summary>
    public Guid? PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }

    public int StatusId { get; set; } = StatusIds.HarvestLot.AwaitingWeighing;
    public Status Status { get; set; } = null!;

    /// <summary>The day the lot was last reopened: the days before automatic close count from it (BR-HARVEST-04).</summary>
    public DateOnly? ReopenedOn { get; set; }

    public DateOnly? CloseDate { get; set; }
    public decimal? LeftoverKg { get; set; }
    public HarvestLotCloseReason? CloseReason { get; set; }

    /// <summary>The invoice whose "sold the whole lot" tick closed the lot, so that invoice alone reopens it (SALE-07, SALE-08); a foreign key to the sales invoices.</summary>
    public Guid? ClosedBySalesInvoiceId { get; set; }

    /// <summary>
    /// The close date the last MSG-HARVEST-05 warning announced (GFR-05): saved with that warning, so a second run, a catch-up or a second app instance never sends it again.
    /// A reopen or a change of the days gives the lot another close date, which is warned once in its turn.
    /// </summary>
    public DateOnly? AutoCloseWarnedFor { get; set; }

    /// <summary>The line of a harvest plan this lot was recorded from; null for a lot outside a plan. A line has at most one lot. Only a link: the lot code does not change.</summary>
    public Guid? HarvestPlanLineId { get; set; }
    public HarvestPlanLine? HarvestPlanLine { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }

    /// <summary>Internal: never shown on the provenance page (BR-TRACE-06).</summary>
    public string? WithdrawalReason { get; set; }

    public ICollection<HarvestLotLane> Lanes { get; set; } = [];
    public ICollection<HarvestLotPhoto> Photos { get; set; } = [];

    /// <summary>The products the lot is split into; empty for a lot recorded before products. <see cref="CageCount"/> and <see cref="EstimatedKg"/> are the sums of the lines when there are any.</summary>
    public ICollection<HarvestLotProductLine> ProductLines { get; set; } = [];

    /// <summary>The cages weighed at the port, one by one; the weighing of the lot is completed from them (owner decision of 24/11/2026).</summary>
    public ICollection<HarvestLotCage> Cages { get; set; } = [];

    /// <summary>The employees who worked on the trip, optional (decision of 12/11/2026).</summary>
    public ICollection<HarvestRecordEmployee> Crew { get; set; } = [];

    /// <summary>Open and Reopened lots can be chosen on a sales invoice (BR-HARVEST-04).</summary>
    public bool IsForSale => StatusId is StatusIds.HarvestLot.Open or StatusIds.HarvestLot.Reopened;

    /// <summary>The origin state is worked out from the two times, never stored (BR-TRACE-02).</summary>
    public HarvestLotOriginState OriginState => OriginStateOf(PublishedAt, WithdrawnAt);

    /// <summary>The origin state of these two times: Withdrawn when the withdrawal is later than the publish time (BR-TRACE-02).</summary>
    public static HarvestLotOriginState OriginStateOf(DateTimeOffset? publishedAt, DateTimeOffset? withdrawnAt) =>
        withdrawnAt > publishedAt ? HarvestLotOriginState.Withdrawn
        : publishedAt is not null ? HarvestLotOriginState.Published
        : HarvestLotOriginState.NotPublished;
}
