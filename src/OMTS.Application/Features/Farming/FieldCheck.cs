namespace OMTS.Application.Features.Farming;

/// <summary>
/// Reads request fields and collects the name of every invalid one, so MSG01 can list them all at once
/// (FARM-02/03/04 "every invalid field listed").
/// </summary>
internal sealed class FieldCheck
{
    public List<string> Invalid { get; } = [];

    public bool IsValid => Invalid.Count == 0;

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

    /// <summary>Latitude −90…90 and longitude −180…180, always both together; both required when <paramref name="required"/>.</summary>
    public void Coordinates(decimal? latitude, decimal? longitude, bool required)
    {
        if (latitude is null && longitude is null)
        {
            if (required)
            {
                Invalid.Add("Latitude");
                Invalid.Add("Longitude");
            }

            return;
        }

        if (latitude is not (>= -90 and <= 90))
        {
            Invalid.Add("Latitude");
        }

        if (longitude is not (>= -180 and <= 180))
        {
            Invalid.Add("Longitude");
        }
    }

    public void Check(bool valid, string field)
    {
        if (!valid)
        {
            Invalid.Add(field);
        }
    }
}
