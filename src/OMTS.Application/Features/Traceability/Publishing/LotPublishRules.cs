using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Publishing;

/// <summary>Why the Trace lot screen does not offer Publish for a lot (TRACE-01 PRE-2).</summary>
public enum LotPublishBlock
{
    /// <summary>No PROVENANCE Publish in the lot's zone: the Accountant, a Zone Manager of another zone (§I.4.4).</summary>
    NoRight,

    /// <summary>A lot is published once (BR-TRACE-02).</summary>
    AlreadyPublished,

    /// <summary>A Not published lot is published from the Trace lot screen only once it is on a sales invoice that is not cancelled.</summary>
    NotOnSalesInvoice,

    /// <summary>Only the Cooperative Owner publishes a Withdrawn lot again, because the Owner withdrew it (BR-TRACE-02).</summary>
    OwnerOnly,
}

public static class LotPublishRules
{
    /// <summary>
    /// TRACE-01.2 step 1 and SCR-TRACE-01: Publish is offered for a Not published lot that is on a sales invoice, or, for the Cooperative Owner, a Withdrawn lot; never for a
    /// Published lot. Null when the caller may publish the lot now; whether it has all its origin information is checked apart (BR-TRACE-01).
    /// </summary>
    /// <param name="zoneId">The zone of the lot's raft.</param>
    /// <param name="onSalesInvoice">The lot is on at least one sales invoice that is not cancelled.</param>
    public static LotPublishBlock? Block(AccountAccess? access, HarvestLot lot, Guid zoneId, bool onSalesInvoice)
    {
        if (access is null || !access.Allows(PermissionFunctions.Provenance, PermissionAction.Publish, PermissionScope.ZoneScoped, zoneId))
        {
            return LotPublishBlock.NoRight;
        }

        return lot.OriginState switch
        {
            HarvestLotOriginState.Published => LotPublishBlock.AlreadyPublished,
            HarvestLotOriginState.Withdrawn => access.IsCooperativeOwner ? null : LotPublishBlock.OwnerOnly,
            _ => onSalesInvoice ? null : LotPublishBlock.NotOnSalesInvoice,
        };
    }
}
