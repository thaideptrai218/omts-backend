using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Publishing;

/// <summary>TRACE-01.2 step 2 on the Trace lot screen: whether Publish is offered, whether the lot passes the check, and what to ask or show.</summary>
/// <param name="OnSalesInvoice">The lot is on at least one sales invoice that is not cancelled.</param>
/// <param name="CanPublish">Publish is offered: a Not published lot on a sales invoice, or a Withdrawn lot for the Cooperative Owner, in a zone where the caller publishes.</param>
/// <param name="Block">Why Publish is not offered, null when it is.</param>
/// <param name="Republish">The lot is Withdrawn: publishing it again (Cooperative Owner).</param>
/// <param name="Ready">The lot has all the origin information the provenance page needs (BR-TRACE-01).</param>
/// <param name="Missing">What it misses, empty when ready.</param>
/// <param name="Message">MSG-TRACE-01 when something is missing; else, when Publish is offered, the confirmation MSG-GEN-04; else null.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record LotPublishCheck(
    Guid LotId,
    string LotCode,
    HarvestLotOriginState OriginState,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? WithdrawnAt,
    bool OnSalesInvoice,
    bool CanPublish,
    LotPublishBlock? Block,
    bool Republish,
    bool Ready,
    IReadOnlyList<OriginGap> Missing,
    string? Message,
    string? MessageCode);

/// <summary>The answer of a publish from the Trace lot screen: the lot as it now is.</summary>
/// <param name="Republished">The lot was Withdrawn and is Published again.</param>
/// <param name="Message">MSG-GEN-05.</param>
public sealed record LotPublished(HarvestLotView Lot, bool Republished, string Message, string MessageCode);
