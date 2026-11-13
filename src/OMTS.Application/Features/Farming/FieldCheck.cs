using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Farming;

/// <summary>
/// Reads request fields and collects the name of every invalid one, so MSG-GEN-01 can list them all at once
/// (FARM-02/03/04 "every invalid field listed").
/// </summary>
internal sealed class FieldCheck
{
    public List<string> Invalid { get; } = [];

    public bool IsValid => Invalid.Count == 0;

    /// <summary>Why a pin was refused beyond its range ((0, 0), or latitude and longitude swapped); shown as the hint of the answer.</summary>
    public string? CoordinateHint { get; private set; }

    /// <summary>MSG-GEN-01 listing every invalid field, with the hint about the pin when there is one.</summary>
    public AppError ToError()
    {
        var error = AppError.InvalidFields(Invalid);
        return CoordinateHint is null ? error : error with { Details = new Dictionary<string, object?>(error.Details!) { ["hint"] = CoordinateHint } };
    }

    /// <summary>Trimmed text of 1…<paramref name="max"/> characters.</summary>
    public string Required(string? value, string field, int max)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > max)
        {
            Invalid.Add(field);
        }

        return trimmed;
    }

    /// <summary>Trimmed text of at most <paramref name="max"/> characters; blank becomes null.</summary>
    public string? Optional(string? value, string field, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > max)
        {
            Invalid.Add(field);
        }

        return trimmed;
    }

    /// <summary>
    /// Latitude −90…90 and longitude −180…180, always both together; both required when <paramref name="required"/>.
    /// Returns them as the columns store them, so the answer, the comparison with the row and the audit log all see the
    /// stored value (GBR-02: saving the same pin again changes nothing). The range is checked on the value as sent.
    /// </summary>
    public (decimal? Latitude, decimal? Longitude) Coordinates(decimal? latitude, decimal? longitude, bool required)
    {
        if (latitude is null && longitude is null)
        {
            if (required)
            {
                Invalid.Add("Latitude");
                Invalid.Add("Longitude");
            }

            return (null, null);
        }

        // PM decision 11/11/2026: (0, 0) and a swapped pair are refused, both fields named, with the reason as a hint.
        if (latitude is { } lat && longitude is { } lon && CoordinateRules.RefusalHint(lat, lon) is { } hint)
        {
            CoordinateHint = hint;
            Invalid.Add("Latitude");
            Invalid.Add("Longitude");
            return (null, null);
        }

        if (latitude is not (>= -90 and <= 90))
        {
            Invalid.Add("Latitude");
        }

        if (longitude is not (>= -180 and <= 180))
        {
            Invalid.Add("Longitude");
        }

        return (AsStored(latitude), AsStored(longitude));
    }

    /// <summary>The Latitude and Longitude columns are decimal(9,6).</summary>
    private static decimal? AsStored(decimal? coordinate) =>
        coordinate is { } value ? Math.Round(value, 6, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// An optional quantity above 0 for a decimal(18, <paramref name="scale"/>) column, returned as the column stores it, so
    /// a value that rounds to 0 or does not fit is refused here and not by the database.
    /// </summary>
    public decimal? PositiveOptional(decimal? value, string field, int scale)
    {
        if (value is null)
        {
            return null;
        }

        var stored = Math.Round(value.Value, scale, MidpointRounding.AwayFromZero);
        if (stored <= 0 || stored >= (decimal)Math.Pow(10, 18 - scale))
        {
            Invalid.Add(field);
        }

        return stored;
    }

    public void Check(bool valid, string field)
    {
        if (!valid)
        {
            Invalid.Add(field);
        }
    }
}
