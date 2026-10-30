
namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>Average, minimum and maximum of one indicator in a period; empty values when <see cref="Count"/> is 0.</summary>
public sealed record IndicatorStatistics(decimal? Average, decimal? Minimum, decimal? Maximum, int Count)
{
    public static readonly IndicatorStatistics Empty = new(null, null, null, 0);
}
