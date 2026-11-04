namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>The dates of a cultivation cycle. The close day still belongs to the cycle (CYCLE-05.1); an Open cycle has no close date.</summary>
public sealed record CycleSpan(Guid Id, Guid RaftId, DateOnly StartDate, DateOnly? CloseDate)
{
    public bool IsOpenOn(DateOnly date) => StartDate <= date && (CloseDate is null || date <= CloseDate);
}
