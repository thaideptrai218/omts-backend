namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>
/// "Cultivation" section. Owner decision of 12/11/2026 (Q21): a new cycle is created by completing a nursery batch, never by hand.
/// <see cref="AllowManualStart"/> keeps the old way (<c>POST /api/rafts/{raftId}/cultivation-cycles</c>) available only where it is set to true: it is false everywhere by default, and only the
/// integration test host of the old cycle tests turns it on. Cycles already running are untouched either way.
/// </summary>
public sealed class CycleStartOptions
{
    public const string SectionName = "Cultivation";

    public bool AllowManualStart { get; set; }
}
