using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>
/// Derived alert state (no column): Active = Confirmed and exceeded, Corrected = replaced by a correction
/// version, Voided = voided; None when the values were within the thresholds (or not confirmed yet).
/// </summary>
public enum WaterAlertState
{
    None,
    Active,
    Corrected,
    Voided,
}
