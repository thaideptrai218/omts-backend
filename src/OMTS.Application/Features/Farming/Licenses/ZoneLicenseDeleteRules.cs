using OMTS.Application.Common.Messages;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>
/// When a license may be deleted (FARM-03): only one that stands outside a renewal chain. A Replaced license and a license that itself replaced another are kept, so the history of
/// the renewals (BR-FARM-06) is never cut.
/// </summary>
public static class ZoneLicenseDeleteRules
{
    /// <summary>MSG-GEN-09 naming why the license stays, or null when it may be deleted.</summary>
    /// <param name="replacement">The license that replaces <paramref name="document"/>, if any.</param>
    public static UserMessage? Refusal(ZoneComplianceDocument document, ZoneComplianceDocument? replacement)
    {
        if (replacement is not null)
        {
            return UserMessages.WrongStatus($"License {document.DocumentNumber}", $"Replaced by {replacement.DocumentNumber}");
        }

        return document.ReplacesDocumentId is not null
            ? UserMessages.WrongStatus($"License {document.DocumentNumber}", "the renewal of another license")
            : null;
    }
}
